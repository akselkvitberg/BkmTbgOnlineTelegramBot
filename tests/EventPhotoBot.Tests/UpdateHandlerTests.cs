using EventPhotoBot.Imaging;
using EventPhotoBot.State;
using EventPhotoBot.Telegram;
using EventPhotoBot.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPhotoBot.Tests;

public class UpdateHandlerTests
{
    private const long Guest = 111;
    private const long Stranger = 999;

    private static byte[] SamplePhoto() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "landscape.jpg"));

    private sealed class Harness
    {
        public InMemoryObjectStore Objects { get; } = new();
        public FakeTelegramClient Telegram { get; } = new();
        public StateStore Store { get; private set; } = null!;
        public UpdateHandler Handler { get; private set; } = null!;

        public static async Task<Harness> CreateAsync(Action<Settings>? configure = null)
        {
            var harness = new Harness();
            harness.Store = new StateStore(harness.Objects);
            await harness.Store.LoadAsync();
            if (configure is not null)
                await harness.Store.MutateAsync(s => configure(s.Settings));
            harness.Handler = new UpdateHandler(
                harness.Store, harness.Objects, harness.Telegram,
                NullLogger<UpdateHandler>.Instance);
            return harness;
        }
    }

    private static TgUpdate PhotoFrom(long userId, string fileUniqueId = "u1",
        string? mediaGroupId = null, string? caption = null) => new()
    {
        UpdateId = 1,
        Message = new TgMessage
        {
            From = new TgUser { Id = userId, FirstName = "Guest" },
            Chat = new TgChat { Id = userId },
            MediaGroupId = mediaGroupId,
            Caption = caption,
            Photo =
            [
                new TgPhotoSize { FileId = "small", FileUniqueId = fileUniqueId, Width = 90, Height = 60 },
                new TgPhotoSize { FileId = "large", FileUniqueId = fileUniqueId, Width = 1600, Height = 1200 },
            ],
        },
    };

    private static void StockFile(Harness harness, string fileId) =>
        harness.Telegram.Files[$"path/{fileId}"] = SamplePhoto();

    [Fact]
    public async Task A_whitelisted_sender_gets_their_photo_queued()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        var image = Assert.Single(harness.Store.Snapshot.Images.Values);
        Assert.Equal(ImageStatus.Pending, image.Status);
        Assert.Equal(Guest, image.SenderId);
        Assert.Equal(ImageSource.Telegram, image.Source);
    }

    [Fact]
    public async Task The_largest_photo_size_is_the_one_downloaded()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));
        StockFile(harness, "large");
        // "small" is deliberately not stocked: using it would throw.

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Single(harness.Store.Snapshot.Images);
    }

    [Fact]
    public async Task All_three_objects_are_written_before_the_state_entry_exists()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        var id = harness.Store.Snapshot.Images.Keys.Single();
        Assert.Contains(ObjectPaths.Display(id), harness.Objects.Paths);
        Assert.Contains(ObjectPaths.Thumb(id), harness.Objects.Paths);
        Assert.Contains(ObjectPaths.Original(id, "jpg"), harness.Objects.Paths);
    }

    [Fact]
    public async Task A_trusted_sender_skips_the_queue_when_auto_approve_is_on()
    {
        var harness = await Harness.CreateAsync(s =>
        {
            s.AutoApproveTrusted = true;
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest", Trusted = true });
        });
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Equal(ImageStatus.Approved, harness.Store.Snapshot.Images.Values.Single().Status);
    }

    [Fact]
    public async Task A_trusted_sender_still_queues_when_auto_approve_is_off()
    {
        var harness = await Harness.CreateAsync(s =>
        {
            s.AutoApproveTrusted = false;
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest", Trusted = true });
        });
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Equal(ImageStatus.Pending, harness.Store.Snapshot.Images.Values.Single().Status);
    }

    [Fact]
    public async Task An_unlisted_sender_is_declined_and_nothing_is_stored()
    {
        var harness = await Harness.CreateAsync();
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Stranger));

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.DoesNotContain(harness.Objects.Paths, p => p.StartsWith("display/"));
        Assert.Single(harness.Telegram.Sent);
    }

    [Fact]
    public async Task Pairing_mode_replies_with_the_id_and_records_the_sender_without_storing_photos()
    {
        var harness = await Harness.CreateAsync(s => s.PairingMode = true);
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Stranger));

        Assert.Empty(harness.Store.Snapshot.Images);
        var seen = Assert.Single(harness.Store.Snapshot.Settings.SeenSenders);
        Assert.Equal(Stranger, seen.Id);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains(Stranger.ToString()));
    }

    [Fact]
    public async Task Pairing_mode_records_a_sender_only_once()
    {
        var harness = await Harness.CreateAsync(s => s.PairingMode = true);
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Stranger));
        await harness.Handler.HandleAsync(PhotoFrom(Stranger));

        Assert.Single(harness.Store.Snapshot.Settings.SeenSenders);
    }

    [Fact]
    public async Task A_duplicate_file_unique_id_is_rejected_without_a_second_copy()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "same"));
        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "same"));

        Assert.Single(harness.Store.Snapshot.Images);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains("already", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_identical_image_sent_with_a_different_file_id_is_caught_by_the_hash()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "first"));
        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "second"));

        Assert.Single(harness.Store.Snapshot.Images);
    }

    [Fact]
    public async Task An_album_gets_one_acknowledgement_rather_than_one_per_photo()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));
        StockFile(harness, "large");

        for (var i = 0; i < 5; i++)
        {
            harness.Telegram.Files[$"path/large{i}"] = SamplePhoto();
            var update = PhotoFrom(Guest, fileUniqueId: $"album{i}", mediaGroupId: "group-1");
            update.Message!.Photo![1].FileId = $"large{i}";
            await harness.Handler.HandleAsync(update);
        }

        Assert.Equal(5, harness.Store.Snapshot.Images.Count);
        Assert.Single(harness.Telegram.Sent);
    }

    [Fact]
    public async Task A_caption_is_stored_with_the_image()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest, caption: "Cake time"));

        Assert.Equal("Cake time", harness.Store.Snapshot.Images.Values.Single().Caption);
    }

    [Fact]
    public async Task A_non_photo_message_is_declined()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));

        await harness.Handler.HandleAsync(new TgUpdate
        {
            Message = new TgMessage
            {
                From = new TgUser { Id = Guest, FirstName = "Guest" },
                Chat = new TgChat { Id = Guest },
                Text = "hello",
            },
        });

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains("photo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_heic_document_is_declined_with_a_clear_message()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));

        await harness.Handler.HandleAsync(new TgUpdate
        {
            Message = new TgMessage
            {
                From = new TgUser { Id = Guest, FirstName = "Guest" },
                Chat = new TgChat { Id = Guest },
                Document = new TgDocument
                {
                    FileId = "doc", FileUniqueId = "doc1",
                    MimeType = "image/heic", FileName = "IMG_0001.HEIC",
                },
            },
        });

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains("HEIC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_file_over_the_twenty_megabyte_bot_limit_is_declined()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));

        var update = PhotoFrom(Guest);
        update.Message!.Photo![1].FileSize = 25 * 1024 * 1024;

        await harness.Handler.HandleAsync(update);

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains("large", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_download_failure_apologises_and_stores_nothing()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));
        // No file stocked, so DownloadAsync throws.

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains("sorry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Start_tells_an_unlisted_sender_they_are_not_on_the_list()
    {
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(new TgUpdate
        {
            Message = new TgMessage
            {
                From = new TgUser { Id = Stranger, FirstName = "Nobody" },
                Chat = new TgChat { Id = Stranger },
                Text = "/start",
            },
        });

        var reply = Assert.Single(harness.Telegram.Sent);
        Assert.Contains("not on the list", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_tells_a_listed_sender_what_happens_to_their_photos()
    {
        var harness = await Harness.CreateAsync(s =>
            s.Whitelist.Add(new WhitelistEntry { Id = Guest, Name = "Guest" }));

        await harness.Handler.HandleAsync(new TgUpdate
        {
            Message = new TgMessage
            {
                From = new TgUser { Id = Guest, FirstName = "Guest" },
                Chat = new TgChat { Id = Guest },
                Text = "/start",
            },
        });

        var reply = Assert.Single(harness.Telegram.Sent);
        Assert.Contains("deleted", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_second_decline_to_the_same_unlisted_sender_is_rate_limited()
    {
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(PhotoFrom(Stranger, fileUniqueId: "a"));
        await harness.Handler.HandleAsync(PhotoFrom(Stranger, fileUniqueId: "b"));

        // Only the first probe gets a reply; the bot does not become a free echo service.
        Assert.Single(harness.Telegram.Sent);
    }

    [Fact]
    public async Task Rate_limiting_an_unlisted_sender_does_not_affect_replies_to_a_different_sender()
    {
        const long OtherStranger = 1000;
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(PhotoFrom(Stranger, fileUniqueId: "a"));
        await harness.Handler.HandleAsync(PhotoFrom(OtherStranger, fileUniqueId: "b"));

        Assert.Equal(2, harness.Telegram.Sent.Count);
    }

    [Fact]
    public async Task Repeated_pairing_mode_probes_from_the_same_sender_are_rate_limited()
    {
        var harness = await Harness.CreateAsync(s => s.PairingMode = true);

        await harness.Handler.HandleAsync(PhotoFrom(Stranger, fileUniqueId: "a"));
        await harness.Handler.HandleAsync(PhotoFrom(Stranger, fileUniqueId: "b"));

        // The sender is still recorded exactly once, but only the first attempt is replied to.
        Assert.Single(harness.Store.Snapshot.Settings.SeenSenders);
        Assert.Single(harness.Telegram.Sent);
    }
}
