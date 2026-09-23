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

        public static async Task<Harness> CreateAsync(Action<EventState>? configure = null)
        {
            var harness = new Harness();
            harness.Store = new StateStore(harness.Objects, seed: new StateSeed(JoinCode));
            await harness.Store.LoadAsync();
            if (configure is not null)
                await harness.Store.MutateAsync(s => configure(s));
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

    private static Sender Roster(long id, bool autoApprove = false, bool banned = false) => new()
    {
        Id = id, Name = "Guest", Banned = banned, FirstSeen = DateTimeOffset.UtcNow,
        CurrentEventId = StateMigration.DefaultEventId,
        Memberships = [new Membership { EventId = StateMigration.DefaultEventId, AutoApprove = autoApprove }],
    };

    private static void StockFile(Harness harness, string fileId) =>
        harness.Telegram.Files[$"path/{fileId}"] = SamplePhoto();

    [Fact]
    public async Task A_known_sender_gets_their_photo_queued()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest)));
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
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, autoApprove: true)));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        var image = Assert.Single(harness.Store.Snapshot.Images.Values);
        Assert.Equal(ImageStatus.Approved, image.Status);
        Assert.NotNull(image.DecidedAt);
    }

    [Fact]
    public async Task A_banned_sender_gets_no_reply_and_nothing_is_stored()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Stranger, banned: true)));
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
        Assert.Empty(harness.Store.Snapshot.Senders);
        var (_, text) = Assert.Single(harness.Telegram.Sent);
        Assert.Contains("QR", text);
    }

    [Fact]
    public async Task The_join_code_admits_a_new_sender_as_known()
    {
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(TextFrom(Stranger, $"/start {Harness.JoinCode}"));

        var sender = Assert.Single(harness.Store.Snapshot.Senders);
        Assert.Equal(Stranger, sender.Id);
        Assert.False(sender.Banned);
        Assert.False(sender.MembershipIn(StateMigration.DefaultEventId)!.AutoApprove);
        Assert.Single(harness.Telegram.Sent);
    }

    [Fact]
    public async Task A_wrong_join_code_admits_nobody()
    {
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(TextFrom(Stranger, "/start wrongcode"));

        Assert.Empty(harness.Store.Snapshot.Senders);
        var (_, text) = Assert.Single(harness.Telegram.Sent);
        Assert.Contains("QR", text);
    }

    [Fact]
    public async Task A_bare_start_admits_nobody()
    {
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(TextFrom(Stranger, "/start"));

        Assert.Empty(harness.Store.Snapshot.Senders);
    }

    [Fact]
    public async Task Redeeming_the_code_again_does_not_demote_an_auto_approve_sender()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest, autoApprove: true)));

        await harness.Handler.HandleAsync(TextFrom(Guest, $"/start {Harness.JoinCode}"));

        Assert.True(Assert.Single(harness.Store.Snapshot.Senders).MembershipIn(StateMigration.DefaultEventId)!.AutoApprove);
    }

    [Fact]
    public async Task A_banned_sender_cannot_readmit_themselves_with_the_code()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Stranger, banned: true)));

        await harness.Handler.HandleAsync(TextFrom(Stranger, $"/start {Harness.JoinCode}"));

        Assert.True(Assert.Single(harness.Store.Snapshot.Senders).Banned);
        Assert.Empty(harness.Telegram.Sent);
    }

    [Fact]
    public async Task Redemption_is_not_blocked_by_the_decline_cooldown()
    {
        var harness = await Harness.CreateAsync();
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Stranger));          // declined, starts the cooldown
        await harness.Handler.HandleAsync(TextFrom(Stranger, $"/start {Harness.JoinCode}"));

        Assert.NotNull(Assert.Single(harness.Store.Snapshot.Senders).MembershipIn(StateMigration.DefaultEventId));
        Assert.Equal(2, harness.Telegram.Sent.Count);
    }

    [Fact]
    public async Task Start_tells_a_known_sender_what_happens_to_their_photos()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest)));

        await harness.Handler.HandleAsync(TextFrom(Guest, "/start"));

        var (_, text) = Assert.Single(harness.Telegram.Sent);
        Assert.StartsWith("Du sender bilder til Daglig.", text);
        Assert.DoesNotContain("slettes", text);
    }

    [Fact]
    public async Task The_largest_photo_size_is_the_one_downloaded()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest)));
        StockFile(harness, "large");
        // "small" is deliberately not stocked: using it would throw.

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Single(harness.Store.Snapshot.Images);
    }

    [Fact]
    public async Task All_three_objects_are_written_before_the_state_entry_exists()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest)));
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
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest)));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "same"));
        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "same"));

        Assert.Single(harness.Store.Snapshot.Images);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains("allerede", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_identical_image_sent_with_a_different_file_id_is_caught_by_the_hash()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest)));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "first"));
        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "second"));

        Assert.Single(harness.Store.Snapshot.Images);
    }

    [Fact]
    public async Task An_album_gets_one_acknowledgement_rather_than_one_per_photo()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest)));

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
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest)));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest, caption: "Cake time"));

        Assert.Equal("Cake time", harness.Store.Snapshot.Images.Values.Single().Caption);
    }

    [Fact]
    public async Task A_non_photo_message_is_declined()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest)));

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
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest)));

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
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest)));

        var update = PhotoFrom(Guest);
        update.Message!.Photo![1].FileSize = 25 * 1024 * 1024;

        await harness.Handler.HandleAsync(update);

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Contains(harness.Telegram.Sent, m => m.Text.Contains("for stort", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_download_failure_apologises_and_stores_nothing()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Guest)));
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

    // ---- Groups ----

    private const long Group = -1001234;

    private static TgChat GroupChat(long id = Group, string title = "Festkomiteen") =>
        new() { Id = id, Type = "supergroup", Title = title };

    private static TgUpdate InGroup(TgUpdate update, long groupId = Group, string title = "Festkomiteen")
    {
        update.Message!.Chat = GroupChat(groupId, title);
        update.Message.MessageId = 77;
        return update;
    }

    private static TgUpdate BotMembership(long groupId, string status, string title = "Festkomiteen") => new()
    {
        MyChatMember = new TgChatMemberUpdated
        {
            Chat = GroupChat(groupId, title),
            NewChatMember = new TgChatMember { Status = status },
        },
    };

    private static BotGroup Routed(long id = Group) => new()
        { Id = id, Title = "Festkomiteen", EventId = StateMigration.DefaultEventId, FirstSeen = DateTimeOffset.UtcNow };

    [Fact]
    public async Task Being_added_to_a_group_records_it_without_listening()
    {
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(BotMembership(Group, "member"));

        var group = Assert.Single(harness.Store.Snapshot.Groups);
        Assert.Equal(Group, group.Id);
        Assert.Equal("Festkomiteen", group.Title);
        Assert.Null(group.EventId);
        Assert.Empty(harness.Telegram.Sent);
    }

    [Fact]
    public async Task Being_removed_from_a_group_forgets_it_listening_included()
    {
        var harness = await Harness.CreateAsync(s => s.Groups.Add(Routed()));

        await harness.Handler.HandleAsync(BotMembership(Group, "kicked"));

        Assert.Empty(harness.Store.Snapshot.Groups);
    }

    [Fact]
    public async Task A_private_chat_membership_change_is_ignored()
    {
        var harness = await Harness.CreateAsync();
        var generation = harness.Store.Generation;

        await harness.Handler.HandleAsync(new TgUpdate
        {
            MyChatMember = new TgChatMemberUpdated
            {
                Chat = new TgChat { Id = Guest, Type = "private" },
                NewChatMember = new TgChatMember { Status = "kicked" },
            },
        });

        Assert.Equal(generation, harness.Store.Generation);
    }

    [Fact]
    public async Task Groups_the_bot_does_not_listen_to_are_capped_and_the_oldest_go_first()
    {
        var harness = await Harness.CreateAsync(s => s.Groups.Add(Routed(-1)));

        for (var i = 0; i < Groups.MaxNotListening + 5; i++)
            await harness.Handler.HandleAsync(BotMembership(-100 - i, "member"));

        var groups = harness.Store.Snapshot.Groups;
        Assert.Equal(Groups.MaxNotListening, groups.Count(g => g.EventId is null));
        Assert.Contains(groups, g => g.Id == -1);                        // listening, never evicted
        Assert.DoesNotContain(groups, g => g.Id == -100);                // the oldest idle one
        Assert.Contains(groups, g => g.Id == -100 - (Groups.MaxNotListening + 4));
    }

    [Fact]
    public async Task A_photo_in_a_group_the_bot_does_not_listen_to_is_ignored_without_a_write()
    {
        var harness = await Harness.CreateAsync(s =>
        {
            s.Senders.Add(Roster(Guest, autoApprove: true));
            s.Groups.Add(new BotGroup { Id = Group, Title = "Festkomiteen" });
        });
        StockFile(harness, "large");
        var generation = harness.Store.Generation;

        await harness.Handler.HandleAsync(InGroup(PhotoFrom(Guest)));

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Equal(generation, harness.Store.Generation);
        Assert.Empty(harness.Telegram.Sent);
        Assert.Empty(harness.Telegram.Reactions);
    }

    [Fact]
    public async Task A_stranger_in_an_unknown_group_gets_no_join_prompt()
    {
        var harness = await Harness.CreateAsync();
        StockFile(harness, "large");
        var generation = harness.Store.Generation;

        await harness.Handler.HandleAsync(InGroup(PhotoFrom(Stranger)));
        await harness.Handler.HandleAsync(InGroup(TextFrom(Stranger, "hei")));

        Assert.Empty(harness.Telegram.Sent);
        Assert.Empty(harness.Store.Snapshot.Senders);
        Assert.Equal(generation, harness.Store.Generation);
    }

    [Theory]
    [InlineData($"/start {Harness.JoinCode}")]
    [InlineData($"/start@eventphotobot {Harness.JoinCode}")]
    [InlineData(Harness.JoinCode)]
    public async Task The_join_code_in_a_group_starts_listening_and_posts_one_notice(string text)
    {
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(InGroup(TextFrom(Stranger, text)));
        await harness.Handler.HandleAsync(InGroup(TextFrom(Stranger, text)));

        var group = Assert.Single(harness.Store.Snapshot.Groups);
        Assert.Equal(StateMigration.DefaultEventId, group.EventId);
        Assert.Equal("Festkomiteen", group.Title);
        var (chatId, notice) = Assert.Single(harness.Telegram.Sent);
        Assert.Equal(Group, chatId);
        Assert.Equal(Groups.ListeningNotice, notice);
        // Posting the code opens the group; it does not put the poster on the roster.
        Assert.Empty(harness.Store.Snapshot.Senders);
    }

    [Theory]
    [InlineData("/start wrongcode")]
    [InlineData("/start")]
    [InlineData($"/start {Harness.JoinCode} extra")]
    [InlineData($"/help {Harness.JoinCode}")]
    public async Task Anything_but_the_join_code_leaves_a_group_closed(string text)
    {
        var harness = await Harness.CreateAsync();

        await harness.Handler.HandleAsync(InGroup(TextFrom(Stranger, text)));

        Assert.Empty(harness.Store.Snapshot.Groups);
        Assert.Empty(harness.Telegram.Sent);
    }

    [Fact]
    public async Task A_banned_member_cannot_open_a_group_with_the_code()
    {
        var harness = await Harness.CreateAsync(s => s.Senders.Add(Roster(Stranger, banned: true)));

        await harness.Handler.HandleAsync(InGroup(TextFrom(Stranger, $"/start {Harness.JoinCode}")));

        Assert.Empty(harness.Store.Snapshot.Groups);
        Assert.Empty(harness.Telegram.Sent);
    }

    [Fact]
    public async Task A_new_members_first_photo_is_queued_and_adds_them_as_known()
    {
        var harness = await Harness.CreateAsync(s => s.Groups.Add(Routed()));
        StockFile(harness, "large");

        var update = InGroup(PhotoFrom(Stranger));
        update.Message!.From!.FirstName = "Kari";
        await harness.Handler.HandleAsync(update);

        var image = Assert.Single(harness.Store.Snapshot.Images.Values);
        Assert.Equal(ImageStatus.Pending, image.Status);
        Assert.Equal(Stranger, image.SenderId);
        var sender = Assert.Single(harness.Store.Snapshot.Senders);
        Assert.Equal(Stranger, sender.Id);
        Assert.Equal("Kari", sender.Name);
        Assert.False(sender.Banned);
        Assert.False(sender.MembershipIn(StateMigration.DefaultEventId)!.AutoApprove);

        Assert.Empty(harness.Telegram.Sent);
        Assert.Equal((Group, 77L, "👀"), Assert.Single(harness.Telegram.Reactions));
    }

    [Fact]
    public async Task An_auto_approve_member_goes_straight_to_the_screen_in_a_group()
    {
        var harness = await Harness.CreateAsync(s =>
        {
            s.Senders.Add(Roster(Guest, autoApprove: true));
            s.Groups.Add(Routed());
        });
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(InGroup(PhotoFrom(Guest)));

        Assert.Equal(ImageStatus.Approved, Assert.Single(harness.Store.Snapshot.Images.Values).Status);
        Assert.Equal("🔥", Assert.Single(harness.Telegram.Reactions).Emoji);
        Assert.Empty(harness.Telegram.Sent);
    }

    [Fact]
    public async Task A_banned_member_is_ignored_in_a_group_without_a_download()
    {
        var harness = await Harness.CreateAsync(s =>
        {
            s.Senders.Add(Roster(Stranger, banned: true));
            s.Groups.Add(Routed());
        });
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(InGroup(PhotoFrom(Stranger)));

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.DoesNotContain(harness.Objects.Paths, IsImageObject);
        Assert.Empty(harness.Telegram.Sent);
        Assert.Empty(harness.Telegram.Reactions);
    }

    [Fact]
    public async Task A_duplicate_in_a_group_is_dropped_silently()
    {
        var harness = await Harness.CreateAsync(s => s.Groups.Add(Routed()));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(InGroup(PhotoFrom(Guest, fileUniqueId: "same")));
        await harness.Handler.HandleAsync(InGroup(PhotoFrom(Guest, fileUniqueId: "same")));

        Assert.Single(harness.Store.Snapshot.Images);
        Assert.Single(harness.Telegram.Reactions);
        Assert.Empty(harness.Telegram.Sent);
    }

    [Fact]
    public async Task Chat_declines_and_failures_in_a_group_get_no_reply()
    {
        var harness = await Harness.CreateAsync(s => s.Groups.Add(Routed()));

        var tooLarge = InGroup(PhotoFrom(Guest));
        tooLarge.Message!.Photo![1].FileSize = 25 * 1024 * 1024;
        var heic = InGroup(TextFrom(Guest, "x"));
        heic.Message!.Text = null;
        heic.Message.Document = new TgDocument { FileId = "doc", FileUniqueId = "doc1", MimeType = "image/heic" };

        await harness.Handler.HandleAsync(InGroup(TextFrom(Guest, "hei alle sammen")));
        await harness.Handler.HandleAsync(InGroup(TextFrom(Guest, "/start")));
        await harness.Handler.HandleAsync(tooLarge);
        await harness.Handler.HandleAsync(heic);
        await harness.Handler.HandleAsync(InGroup(PhotoFrom(Guest, fileUniqueId: "nofile")));  // download throws

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Empty(harness.Telegram.Sent);
        Assert.Empty(harness.Telegram.Reactions);
        // Nobody was added for posting nothing usable.
        Assert.Empty(harness.Store.Snapshot.Senders);
    }

    [Fact]
    public async Task Posts_made_as_a_chat_or_by_a_bot_are_ignored()
    {
        var harness = await Harness.CreateAsync(s => s.Groups.Add(Routed()));
        StockFile(harness, "large");

        var anonymous = InGroup(PhotoFrom(1087968824, fileUniqueId: "a"));
        anonymous.Message!.SenderChat = GroupChat();
        var bot = InGroup(PhotoFrom(4242, fileUniqueId: "b"));
        bot.Message!.From!.IsBot = true;

        await harness.Handler.HandleAsync(anonymous);
        await harness.Handler.HandleAsync(bot);

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.Empty(harness.Store.Snapshot.Senders);
    }

    [Fact]
    public async Task A_renamed_group_gets_its_new_title_with_the_next_photo()
    {
        var harness = await Harness.CreateAsync(s => s.Groups.Add(Routed()));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(InGroup(PhotoFrom(Guest), title: "Festkomiteen 2026"));

        Assert.Equal("Festkomiteen 2026", Assert.Single(harness.Store.Snapshot.Groups).Title);
    }

    [Fact]
    public async Task An_upgrade_to_a_supergroup_carries_listening_over_to_the_new_id()
    {
        const long OldId = -4321, NewId = -1004321;
        var harness = await Harness.CreateAsync(s => s.Groups.Add(Routed(OldId)));

        var to = InGroup(TextFrom(Guest, "x"), groupId: OldId);
        to.Message!.Text = null;
        to.Message.Chat!.Type = "group";
        to.Message.MigrateToChatId = NewId;
        var from = InGroup(TextFrom(Guest, "x"), groupId: NewId);
        from.Message!.Text = null;
        from.Message.MigrateFromChatId = OldId;

        await harness.Handler.HandleAsync(to);
        await harness.Handler.HandleAsync(from);

        var group = Assert.Single(harness.Store.Snapshot.Groups);
        Assert.Equal(NewId, group.Id);
        Assert.Equal(StateMigration.DefaultEventId, group.EventId);
        Assert.Empty(harness.Telegram.Sent);
    }

    [Fact]
    public async Task A_private_chat_with_an_explicit_type_behaves_as_before()
    {
        var harness = await Harness.CreateAsync(s => s.Groups.Add(Routed()));
        StockFile(harness, "large");

        var update = PhotoFrom(Stranger);
        update.Message!.Chat!.Type = "private";
        await harness.Handler.HandleAsync(update);

        var (chatId, text) = Assert.Single(harness.Telegram.Sent);
        Assert.Equal(Stranger, chatId);
        Assert.Contains("QR", text);
        Assert.Empty(harness.Telegram.Reactions);
    }

    [Fact]
    public async Task A_chat_of_an_unknown_type_is_ignored()
    {
        var harness = await Harness.CreateAsync();

        var update = TextFrom(Stranger, $"/start {Harness.JoinCode}");
        update.Message!.Chat = new TgChat { Id = -100777, Type = "channel" };
        await harness.Handler.HandleAsync(update);

        Assert.Empty(harness.Telegram.Sent);
        Assert.Empty(harness.Store.Snapshot.Senders);
        Assert.Empty(harness.Store.Snapshot.Groups);
    }

    // ---- Events ----

    private const string WeddingCode = "wedding-code";

    // Controller ruling: Wedding must return the event, since
    // A_code_for_a_scheduled_event_admits_nobody_yet sets OpensAt on the result.
    private static Event Wedding(EventState s, bool closed = false) =>
        s.AddEvent("bryllup", name: "Bryllup", joinCode: WeddingCode, closed: closed);

    [Fact]
    public async Task A_new_sender_joining_a_special_event_is_a_member_of_that_event_only()
    {
        var harness = await Harness.CreateAsync(s => Wedding(s));

        await harness.Handler.HandleAsync(TextFrom(Stranger, $"/start {WeddingCode}"));

        var sender = Assert.Single(harness.Store.Snapshot.Senders);
        Assert.Equal(["bryllup"], sender.Memberships.Select(m => m.EventId));
        Assert.Equal("bryllup", sender.CurrentEventId);
        Assert.Contains("Bryllup", Assert.Single(harness.Telegram.Sent).Text);
    }

    [Fact]
    public async Task A_daily_member_switching_to_a_special_event_is_told_where_photos_go()
    {
        var harness = await Harness.CreateAsync(s => { Wedding(s); s.Senders.Add(Roster(Guest)); });

        await harness.Handler.HandleAsync(TextFrom(Guest, $"/start {WeddingCode}"));

        var sender = Assert.Single(harness.Store.Snapshot.Senders);
        Assert.Equal("bryllup", sender.CurrentEventId);
        Assert.Equal(2, sender.Memberships.Count);
        Assert.Equal("Du sender nå bilder til Bryllup.", Assert.Single(harness.Telegram.Sent).Text);
    }

    [Fact]
    public async Task A_photo_goes_to_the_current_event_and_the_reply_names_it()
    {
        var harness = await Harness.CreateAsync(s => { Wedding(s); s.Senders.Add(Roster(Guest)); });
        await harness.Handler.HandleAsync(TextFrom(Guest, $"/start {WeddingCode}"));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Equal("bryllup", Assert.Single(harness.Store.Snapshot.Images.Values).EventId);
        Assert.StartsWith("Mottatt til Bryllup", harness.Telegram.Sent[^1].Text);
    }

    [Fact]
    public async Task Once_the_special_event_closes_a_daily_member_falls_back_silently()
    {
        var harness = await Harness.CreateAsync(s =>
        {
            Wedding(s, closed: true);
            var guest = Roster(Guest);
            guest.CurrentEventId = "bryllup";
            guest.Memberships.Add(new Membership { EventId = "bryllup" });
            s.Senders.Add(guest);
        });
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Equal("daglig", Assert.Single(harness.Store.Snapshot.Images.Values).EventId);
        // Only the acknowledgement; no "the wedding has ended" notice.
        Assert.StartsWith("Mottatt til Daglig", Assert.Single(harness.Telegram.Sent).Text);
    }

    [Fact]
    public async Task Once_the_special_event_closes_a_guest_of_only_that_event_is_declined()
    {
        var harness = await Harness.CreateAsync(s =>
        {
            Wedding(s, closed: true);
            s.Senders.Add(new Sender
            {
                Id = Guest, Name = "Guest", CurrentEventId = "bryllup",
                Memberships = [new Membership { EventId = "bryllup" }],
            });
        });
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Empty(harness.Store.Snapshot.Images);
        Assert.DoesNotContain(harness.Objects.Paths, IsImageObject);
        Assert.Equal("Bryllup er avsluttet.", Assert.Single(harness.Telegram.Sent).Text);
    }

    [Fact]
    public async Task A_code_for_a_closed_event_admits_nobody()
    {
        var harness = await Harness.CreateAsync(s => Wedding(s, closed: true));

        await harness.Handler.HandleAsync(TextFrom(Stranger, $"/start {WeddingCode}"));

        Assert.Empty(harness.Store.Snapshot.Senders);
        Assert.Equal("Bryllup er avsluttet.", Assert.Single(harness.Telegram.Sent).Text);
    }

    [Fact]
    public async Task A_code_for_a_scheduled_event_admits_nobody_yet()
    {
        var harness = await Harness.CreateAsync(s => Wedding(s).OpensAt = DateTimeOffset.UtcNow.AddDays(1));

        await harness.Handler.HandleAsync(TextFrom(Stranger, $"/start {WeddingCode}"));

        Assert.Empty(harness.Store.Snapshot.Senders);
        Assert.Equal("Bryllup har ikke startet ennå.", Assert.Single(harness.Telegram.Sent).Text);
    }

    [Fact]
    public async Task Auto_approve_in_the_daily_event_does_not_carry_over_to_a_special_one()
    {
        var harness = await Harness.CreateAsync(s => { Wedding(s); s.Senders.Add(Roster(Guest, autoApprove: true)); });
        await harness.Handler.HandleAsync(TextFrom(Guest, $"/start {WeddingCode}"));
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Equal(ImageStatus.Pending, Assert.Single(harness.Store.Snapshot.Images.Values).Status);
    }

    [Fact]
    public async Task The_same_photo_can_be_sent_to_two_events()
    {
        var harness = await Harness.CreateAsync(s => { Wedding(s); s.Senders.Add(Roster(Guest)); });
        StockFile(harness, "large");

        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "same"));
        await harness.Handler.HandleAsync(TextFrom(Guest, $"/start {WeddingCode}"));
        await harness.Handler.HandleAsync(PhotoFrom(Guest, fileUniqueId: "same"));

        Assert.Equal(["bryllup", "daglig"], harness.Store.Snapshot.Images.Values.Select(i => i.EventId).Order());
    }

    [Fact]
    public async Task An_event_closing_during_the_download_is_seen_under_the_lock()
    {
        var harness = await Harness.CreateAsync(s => { Wedding(s); s.Senders.Add(Roster(Guest)); });
        await harness.Handler.HandleAsync(TextFrom(Guest, $"/start {WeddingCode}"));
        StockFile(harness, "large");
        harness.Telegram.OnDownload = () => harness.Store
            .MutateAsync(s => s.Find("bryllup")!.ClosedAt = DateTimeOffset.UtcNow.AddSeconds(-1))
            .GetAwaiter().GetResult();

        await harness.Handler.HandleAsync(PhotoFrom(Guest));

        Assert.Equal("daglig", Assert.Single(harness.Store.Snapshot.Images.Values).EventId);
        Assert.StartsWith("Mottatt til Daglig", harness.Telegram.Sent[^1].Text);
    }
}
