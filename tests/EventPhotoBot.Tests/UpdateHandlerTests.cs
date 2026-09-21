using EventPhotoBot.Imaging;
using EventPhotoBot.State;
using EventPhotoBot.Telegram;
using EventPhotoBot.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace EventPhotoBot.Tests;

public class UpdateHandlerTests
{
    private const long Guest = 111;
    private const long Stranger = 999;

    private static byte[] SamplePhoto() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "landscape.jpg"));

    /// <summary>
    /// A small, valid, decodable JPEG whose pixel content — and so whose sha256 after
    /// ImagePipeline.Process — differs by seed. Used where a test needs several genuinely
    /// distinct images rather than one fixture reused several times, since the content-hash
    /// duplicate guard now applies uniformly, including to album items.
    /// </summary>
    private static byte[] DistinctPhoto(int seed)
    {
        var color = new Rgb24(
            (byte)(seed * 37 % 256), (byte)(seed * 91 % 256), (byte)(seed * 173 % 256));
        using var image = new Image<Rgb24>(64, 48, color);
        using var stream = new MemoryStream();
        image.Save(stream, new JpegEncoder());
        return stream.ToArray();
    }

    private sealed class Harness
    {
        public const string JoinCode = "party2026";

        public InMemoryObjectStore Objects { get; } = new();
        public FakeTelegramClient Telegram { get; } = new();
        public StateStore Store { get; private set; } = null!;
        public UpdateHandler Handler { get; private set; } = null!;

        private static AppConfig Config() => new()
        {
            BucketName = "bucket", EventName = "Party", BotToken = "token",
            WebhookSecret = "secret", WebhookPath = "abc123", AdminPassword = "hunter2",
            CookieSigningKey = "0123456789abcdef0123456789abcdef", JoinCode = JoinCode,
        };

        public static async Task<Harness> CreateAsync(Action<Settings>? configure = null)
        {
            var harness = new Harness();
            harness.Store = new StateStore(harness.Objects);
            await harness.Store.LoadAsync();
            if (configure is not null)
                await harness.Store.MutateAsync(s => configure(s.Settings));
            harness.Handler = new UpdateHandler(
                harness.Store, harness.Objects, harness.Telegram, Config(),
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

    private static TgUpdate TextFrom(long userId, string text) => new()
    {
        UpdateId = 1,
        Message = new TgMessage
        {
            From = new TgUser { Id = userId, FirstName = "Guest" },
            Chat = new TgChat { Id = userId },
            Text = text,
        },
    };

    /// <summary>Any object holding photo bytes, as opposed to state/state.json.</summary>
    private static bool IsImageObject(string path) =>
        path.StartsWith("originals/") || path.StartsWith("display/") || path.StartsWith("thumbs/");

    private static Sender Roster(long id, SenderStatus status) =>
        new() { Id = id, Name = "Guest", Status = status, FirstSeen = DateTimeOffset.UtcNow };

    private static void StockFile(Harness harness, string fileId) =>
        harness.Telegram.Files[$"path/{fileId}"] = SamplePhoto();

    [Fact]
    public async Task A_known_sender_gets_their_photo_queued()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.Known)));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        var image = Assert.Single(harness.Store.Snapshot.Images.Values);
        Assert.Equal(ImageStatus.Pending, image.Status);
        Assert.Equal(Guest, image.SenderId);
        Assert.Equal(ImageSource.Telegram, image.Source);
    }

    [Fact]
    public async Task An_auto_approve_sender_skips_the_queue()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.AutoApprove)));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        var image = Assert.Single(harness.Store.Snapshot.Images.Values);
        Assert.Equal(ImageStatus.Approved, image.Status);
        Assert.NotNull(image.DecidedAt);
    }

    [Fact]
    public async Task A_banned_sender_gets_no_reply_and_nothing_is_stored()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Stranger, SenderStatus.Banned)));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Stranger));

        Assert.Empty(harness.Store.Snapshot.Images);
        // state/state.json is there from seeding the roster; what must not appear is
        // any of the image bytes — a banned sender never gets as far as a download.
        Assert.DoesNotContain(harness.Objects.Paths, IsImageObject);
        Assert.Empty(harness.Telegram.Sent);
    }

    [Fact]
    public async Task An_unredeemed_sender_is_told_to_scan_the_qr_and_nothing_is_stored()
    {
        var harness = await Harness.CreateAsync();
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Stranger));

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.DoesNotContain(harness.Objects.Paths, IsImageObject);
        Assert.Empty(harness.Store.Snapshot.Settings.Senders);
        var (_, text) = Assert.Single(harness.Telegram.Sent);
        Assert.Contains("QR", text);
    }

    [Fact]
    public async Task The_join_code_admits_a_new_sender_as_known()
    {
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(TextFrom(Stranger, $"/start {Harness.JoinCode}"));

        var sender = Assert.Single(harness.Store.Snapshot.Settings.Senders);
        Assert.Equal(Stranger, sender.Id);
        Assert.Equal(SenderStatus.Known, sender.Status);
        Assert.Single(harness.Telegram.Sent);
    }

    [Fact]
    public async Task A_wrong_join_code_admits_nobody()
    {
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(TextFrom(Stranger, "/start wrongcode"));

        Assert.Empty(harness.Store.Snapshot.Settings.Senders);
        var (_, text) = Assert.Single(harness.Telegram.Sent);
        Assert.Contains("QR", text);
    }

    [Fact]
    public async Task A_bare_start_admits_nobody()
    {
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(TextFrom(Stranger, "/start"));

        Assert.Empty(harness.Store.Snapshot.Settings.Senders);
    }

    [Fact]
    public async Task Redeeming_the_code_again_does_not_demote_an_auto_approve_sender()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.AutoApprove)));

        await harness.Handler.HandleAsync(TextFrom(Guest, $"/start {Harness.JoinCode}"));

        Assert.Equal(SenderStatus.AutoApprove,
            Assert.Single(harness.Store.Snapshot.Settings.Senders).Status);
    }

    [Fact]
    public async Task A_banned_sender_cannot_readmit_themselves_with_the_code()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Stranger, SenderStatus.Banned)));

        await harness.Handler.HandleAsync(TextFrom(Stranger, $"/start {Harness.JoinCode}"));

        Assert.Equal(SenderStatus.Banned,
            Assert.Single(harness.Store.Snapshot.Settings.Senders).Status);
        Assert.Empty(harness.Telegram.Sent);
    }

    [Fact]
    public async Task Redemption_is_not_blocked_by_the_decline_cooldown()
    {
        var harness = await Harness.CreateAsync();
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Stranger));          // declined, starts the cooldown
        await harness.Handler.HandleAsync(TextFrom(Stranger, $"/start {Harness.JoinCode}"));

        Assert.Equal(SenderStatus.Known,
            Assert.Single(harness.Store.Snapshot.Settings.Senders).Status);
        Assert.Equal(2, harness.Telegram.Sent.Count);
    }

    [Fact]
    public async Task Start_tells_a_known_sender_what_happens_to_their_photos()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.Known)));

        await harness.Handler.HandleAsync(TextFrom(Guest, "/start"));

        var (_, text) = Assert.Single(harness.Telegram.Sent);
        Assert.Contains("slettes etter arrangementet", text);
    }

    [Fact]
    public async Task The_largest_photo_size_is_the_one_downloaded()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.Known)));
        StockFile(harness, "large");
        // "small" is deliberately not stocked: using it would throw.

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Single(harness.Store.Snapshot.Images);
    }

    [Fact]
    public async Task All_three_objects_are_written_before_the_state_entry_exists()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.Known)));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        var id = harness.Store.Snapshot.Images.Keys.Single();
        Assert.Contains(ObjectPaths.Display(id), harness.Objects.Paths);
        Assert.Contains(ObjectPaths.Thumb(id), harness.Objects.Paths);
        Assert.Contains(ObjectPaths.Original(id, "jpg"), harness.Objects.Paths);
    }

    [Fact]
    public async Task A_duplicate_file_unique_id_is_rejected_without_a_second_copy()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.Known)));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "same"));
        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "same"));

        Assert.Single(harness.Store.Snapshot.Images);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains("allerede", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_identical_image_sent_with_a_different_file_id_is_caught_by_the_hash()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.Known)));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "first"));
        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "second"));

        Assert.Single(harness.Store.Snapshot.Images);
    }

    [Fact]
    public async Task An_album_gets_one_acknowledgement_rather_than_one_per_photo()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.Known)));

        // Five genuinely distinct images, the way a real five-photo album is: the
        // content-hash duplicate guard applies here too, so five copies of the same
        // fixture would (correctly) collapse into one stored image.
        for (var i = 0; i < 5; i++)
        {
            harness.Telegram.Files[$"path/large{i}"] = DistinctPhoto(i);
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
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.Known)));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest, caption: "Cake time"));

        Assert.Equal("Cake time", harness.Store.Snapshot.Images.Values.Single().Caption);
    }

    [Fact]
    public async Task A_non_photo_message_is_declined()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.Known)));

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
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains("bare bilder", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_heic_document_is_declined_with_a_clear_message()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.Known)));

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
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.Known)));

        var update = PhotoFrom(Guest);
        update.Message!.Photo![1].FileSize = 25 * 1024 * 1024;

        await harness.Handler.HandleAsync(update);

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains("for stort", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_download_failure_apologises_and_stores_nothing()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, SenderStatus.Known)));
        // No file stocked, so DownloadAsync throws.

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains("beklager", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_second_decline_to_the_same_unredeemed_sender_is_rate_limited()
    {
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(PhotoFrom(Stranger, fileUniqueId: "a"));
        await harness.Handler.HandleAsync(PhotoFrom(Stranger, fileUniqueId: "b"));

        // Only the first probe gets a reply; the bot does not become a free echo service.
        Assert.Single(harness.Telegram.Sent);
    }

    [Fact]
    public async Task Rate_limiting_an_unredeemed_sender_does_not_affect_replies_to_a_different_sender()
    {
        const long OtherStranger = 1000;
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(PhotoFrom(Stranger, fileUniqueId: "a"));
        await harness.Handler.HandleAsync(PhotoFrom(OtherStranger, fileUniqueId: "b"));

        Assert.Equal(2, harness.Telegram.Sent.Count);
    }
}
