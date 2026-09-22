using System.Security.Cryptography;
using System.Text;
using EventPhotoBot.Imaging;
using EventPhotoBot.State;

namespace EventPhotoBot.Telegram;

public sealed class UpdateHandler(
    StateStore store,
    IObjectStore objects,
    ITelegramClient telegram,
    AppConfig config,
    ILogger<UpdateHandler> logger)
{
    private const long MaxDownloadBytes = 20L * 1024 * 1024;

    // UpdateHandler is registered as a singleton and ASP.NET Core dispatches
    // concurrent requests across thread-pool threads, so two album members (or two
    // unrelated senders) can genuinely run this class's methods at the same time.
    // Both fields below are mutated from request-handling code, so every access to
    // either is behind its own lock — a HashSet/Dictionary is not thread-safe on its
    // own, and neither field is written to disk, so a lock here costs nothing beyond
    // the in-process contention.
    private readonly object _groupLock = new();
    private readonly object _replyThrottleLock = new();

    /// <summary>
    /// Album sends arrive as separate updates sharing a media_group_id. Each is its
    /// own image; the group exists only so a five-photo album gets one reply.
    /// In-memory and unbounded, which is fine: one instance, one event.
    /// </summary>
    private readonly HashSet<string> _acknowledgedGroups = [];

    /// <summary>
    /// The bot handle is effectively public once shared, and a decline that answers
    /// every message is a free way to make the bot talk. Throttles the "scan the QR"
    /// reply to a sender who has not redeemed the join code; in-memory only, so a
    /// redeploy simply resets the cooldown. Redemption itself is never throttled —
    /// see HandleUnredeemedAsync.
    /// </summary>
    private readonly Dictionary<long, DateTimeOffset> _lastUnlistedReplyAt = [];
    private static readonly TimeSpan UnlistedReplyCooldown = TimeSpan.FromSeconds(60);

    // A photo in a group is acknowledged with a reaction on it rather than a reply.
    // Two, so a pre-approved photographer can tell "on the screen" from "queued".
    private const string QueuedReaction = "👀";
    private const string LiveReaction = "🔥";

    private const string JoinPrompt =
        "Du står ikke på listen for dette arrangementet ennå. Skann QR-koden på skjermen — " +
        "den sender koden til meg, og du kan begynne å sende bilder med en gang.";

    public async Task HandleAsync(TgUpdate update, CancellationToken ct = default)
    {
        if (update.MyChatMember is { } membership)
        {
            await HandleMembershipAsync(membership, ct);
            return;
        }

        var message = update.Message;
        if (message?.Chat is { IsGroup: true } group)
        {
            await HandleGroupMessageAsync(message, group, ct);
            return;
        }

        // Anything neither a group nor a private chat is dropped, rather than falling
        // into the private flow below and getting the join prompt posted into it.
        if (message?.From is not { } sender || message.Chat is not { IsPrivate: true } chat) return;

        var entry = store.Snapshot.Settings.Senders.FirstOrDefault(s => s.Id == sender.Id);

        // Banned first, and before anything that costs a download, a reply or a
        // write. A banned sender gets no signal at all — a reply would both confirm
        // the ban landed and make the bot a reply relay for whoever earned it.
        if (entry is { Status: SenderStatus.Banned }) return;

        if (entry is null)
        {
            await HandleUnredeemedAsync(message, sender, chat, ct);
            return;
        }

        if (message.Text is { } text && text.StartsWith("/start", StringComparison.Ordinal))
        {
            await telegram.SendMessageAsync(chat.Id,
                "Send meg bilder, så kommer de opp på skjermen på arrangementet. " +
                "En arrangør godkjenner dem først. Alt slettes etter arrangementet.", ct);
            return;
        }

        var candidate = Extract(message);
        if (candidate is null)
        {
            await telegram.SendMessageAsync(chat.Id,
                "Bare bilder, takk — jeg kan ikke vise video, klistremerker eller animasjoner.", ct);
            return;
        }

        if (candidate.DeclineReason is { } reason)
        {
            await telegram.SendMessageAsync(chat.Id, reason, ct);
            return;
        }

        await IngestAsync(message, sender, chat, entry, candidate, ct);
    }

    /// <summary>
    /// The bot was added to or removed from a chat. Telegram offers no way to ask
    /// which groups a bot is in, so this is where the admin page's list comes from.
    /// Private chats are ignored: there it means a guest blocked or unblocked the bot,
    /// which changes nothing about whether their photos are wanted.
    /// </summary>
    private async Task HandleMembershipAsync(TgChatMemberUpdated membership, CancellationToken ct)
    {
        if (membership.Chat is not { IsGroup: true } chat) return;

        if (membership.NewChatMember?.IsInChat == true)
        {
            await store.MutateAsync(state =>
                Groups.Remember(state, chat.Id, chat.Title, DateTimeOffset.UtcNow), ct);
            return;
        }

        // Removed or left. The row goes, listening included: a bot that is re-added
        // later has to be given the code again, and the notice goes out again with it.
        if (store.Snapshot.Settings.Groups.All(g => g.Id != chat.Id)) return;
        await store.MutateAsync(state => state.Settings.Groups.RemoveAll(g => g.Id == chat.Id), ct);
    }

    /// <summary>
    /// Everything posted in a group. Nothing here ever sends a text reply except the
    /// one notice when listening starts: a group is other people's conversation, and
    /// a bot that answers in it — a join prompt, a "received", a decline — is noise
    /// for every member, and in a group the bot does not listen to, a way to make it
    /// talk to strangers.
    /// </summary>
    private async Task HandleGroupMessageAsync(TgMessage message, TgChat chat, CancellationToken ct)
    {
        if (message.MigrateToChatId is { } to)
        {
            await MigrateGroupAsync(chat.Id, to, ct);
            return;
        }
        if (message.MigrateFromChatId is { } from)
        {
            await MigrateGroupAsync(from, chat.Id, ct);
            return;
        }

        // A message posted as a chat (an anonymous admin, a linked channel) carries a
        // placeholder "from" shared by everyone who posts that way. Recording it as a
        // person would lump them together, and banning it would ban all of them.
        if (message.From is not { IsBot: false } sender || message.SenderChat is not null) return;

        var entry = store.Snapshot.Settings.Senders.FirstOrDefault(s => s.Id == sender.Id);

        // Banned first, as in a private chat, and before the join code: a banned
        // person must not be able to open a group of their own to the queue.
        if (entry is { Status: SenderStatus.Banned }) return;

        var group = store.Snapshot.Settings.Groups.FirstOrDefault(g => g.Id == chat.Id);
        if (group is not { Listening: true })
        {
            if (message.Text is { } text && TryReadGroupJoinCode(text, out var supplied)
                && JoinCodeMatches(config.JoinCode, supplied))
                await StartListeningAsync(chat, ct);
            return;
        }

        // Members talking to each other, and the join code posted again. Neither is
        // addressed to the bot.
        if (message.Text is not null) return;

        var candidate = Extract(message);
        if (candidate is null) return;
        if (candidate.DeclineReason is not null)
        {
            logger.LogInformation("Ignored an unusable file from {Sender} in group {Chat}.", sender.Id, chat.Id);
            return;
        }

        await IngestAsync(message, sender, chat, entry, candidate, ct);
    }

    private async Task StartListeningAsync(TgChat chat, CancellationToken ct)
    {
        var started = await store.MutateAsync(state =>
            Groups.Listen(state, chat.Id, chat.Title, DateTimeOffset.UtcNow), ct);

        if (started) await telegram.SendMessageAsync(chat.Id, Groups.ListeningNotice, ct);
    }

    /// <summary>
    /// A basic group upgraded to a supergroup keeps its members but gets a new chat
    /// id. Without this, a group the organisers set up would silently stop being
    /// listened to the moment someone changed a setting that forces the upgrade.
    /// Telegram posts the pair of service messages itself; members cannot forge them.
    /// </summary>
    private async Task MigrateGroupAsync(long from, long to, CancellationToken ct)
    {
        // Both halves of the pair arrive; the second finds nothing left to move.
        if (store.Snapshot.Settings.Groups.All(g => g.Id != from)) return;

        await store.MutateAsync(state =>
        {
            var groups = state.Settings.Groups;
            var old = groups.FirstOrDefault(g => g.Id == from);
            if (old is null) return;

            if (groups.FirstOrDefault(g => g.Id == to) is { } existing)
            {
                existing.Listening |= old.Listening;
                groups.Remove(old);
            }
            else
            {
                old.Id = to;
            }
        }, ct);
    }

    /// <summary>
    /// "/start CODE" (or "/start@bot CODE", as a group client writes a command), or the
    /// bare code on its own, as someone would type it after reading it off the screen.
    /// The bare form only reaches the bot once privacy mode is off; a command always does.
    /// </summary>
    private static bool TryReadGroupJoinCode(string text, out string code)
    {
        code = "";
        var parts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries
                                       | StringSplitOptions.TrimEntries);

        if (parts.Length == 2
            && (parts[0] == "/start" || parts[0].StartsWith("/start@", StringComparison.Ordinal)))
            code = parts[1];
        else if (parts.Length == 1 && !parts[0].StartsWith('/'))
            code = parts[0];
        else
            return false;

        return true;
    }

    /// <summary>
    /// Nobody in the roster. Either they are redeeming the join code, or they found
    /// the bot some other way and need pointing at the QR. Nothing is stored in the
    /// second case: recording every stranger who pokes the bot would let anyone who
    /// finds the handle grow the roster with writes nobody authorised.
    /// </summary>
    private async Task HandleUnredeemedAsync(
        TgMessage message, TgUser sender, TgChat chat, CancellationToken ct)
    {
        if (message.Text is { } text && TryReadJoinCode(text, out var supplied)
            && JoinCodeMatches(config.JoinCode, supplied))
        {
            await store.MutateAsync(state =>
            {
                // Re-checked under the store's lock: two /start messages racing must
                // not produce two rows, and a row added by an admin in the meantime
                // must not be overwritten with a weaker status.
                if (state.Settings.Senders.Any(s => s.Id == sender.Id)) return;
                state.Settings.Senders.Add(new Sender
                {
                    Id = sender.Id,
                    Name = sender.DisplayName,
                    Status = SenderStatus.Known,
                    FirstSeen = DateTimeOffset.UtcNow,
                });
            }, ct);

            // Deliberately outside the throttle: being rate-limited out of joining is
            // the worst possible moment to go quiet on somebody.
            await telegram.SendMessageAsync(chat.Id,
                "Du er inne. Send meg bilder, så kommer de opp på skjermen så snart en " +
                "arrangør har godkjent dem. Alt slettes etter arrangementet.", ct);
            return;
        }

        if (ShouldReplyToUnlisted(sender.Id))
            await telegram.SendMessageAsync(chat.Id, JoinPrompt, ct);
    }

    /// <summary>"/start CODE" — the payload Telegram appends from a deep link.</summary>
    private static bool TryReadJoinCode(string text, out string code)
    {
        code = "";
        if (!text.StartsWith("/start", StringComparison.Ordinal)) return false;

        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries
                                       | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;

        code = parts[1];
        return true;
    }

    /// <summary>
    /// Constant-time, hashing both sides first. Same reasoning as the webhook
    /// secret check in Program.cs: this is reachable by anyone who finds the bot,
    /// and FixedTimeEquals on its own leaks length through its argument check.
    /// </summary>
    private static bool JoinCodeMatches(string expected, string supplied)
    {
        Span<byte> hashA = stackalloc byte[32];
        Span<byte> hashB = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), hashA);
        SHA256.HashData(Encoding.UTF8.GetBytes(supplied), hashB);
        return CryptographicOperations.FixedTimeEquals(hashA, hashB);
    }

    /// <summary>
    /// True at most once per cooldown window per sender; also records the attempt.
    /// The check-and-record is one atomic step under the lock, so two concurrent
    /// probes from the same sender cannot both read "no recent reply" and both win.
    /// </summary>
    private bool ShouldReplyToUnlisted(long senderId)
    {
        lock (_replyThrottleLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastUnlistedReplyAt.TryGetValue(senderId, out var last)
                && now - last < UnlistedReplyCooldown)
                return false;

            _lastUnlistedReplyAt[senderId] = now;
            return true;
        }
    }

    private sealed record Candidate(
        string FileId, string FileUniqueId, string Extension, string? DeclineReason);

    /// <summary>
    /// Photo messages carry several sizes; take the largest. Documents are accepted
    /// only for the formats the pipeline can actually decode.
    /// </summary>
    private static Candidate? Extract(TgMessage message)
    {
        if (message.Photo is { Count: > 0 } photos)
        {
            var largest = photos.MaxBy(p => (long)p.Width * p.Height)!;
            return new Candidate(largest.FileId, largest.FileUniqueId, "jpg",
                largest.FileSize > MaxDownloadBytes
                    ? "Det bildet er for stort til at jeg får hentet det — Telegram setter en grense på 20 MB for bot-nedlastinger."
                    : null);
        }

        if (message.Document is { } document)
        {
            if (string.Equals(document.MimeType, "image/heic", StringComparison.OrdinalIgnoreCase))
                return new Candidate(document.FileId, document.FileUniqueId, "heic",
                    "Jeg kan ikke lese HEIC-filer. Send bildet som bilde i stedet for som fil, " +
                    "eller konverter det til JPEG først.");

            if (!ImagePipeline.IsSupportedMimeType(document.MimeType))
                return null;

            var extension = document.MimeType!.Split('/')[^1].ToLowerInvariant();
            return new Candidate(document.FileId, document.FileUniqueId, extension,
                document.FileSize > MaxDownloadBytes
                    ? "Den filen er for stor til at jeg får hentet den — Telegram setter en grense på 20 MB for bot-nedlastinger."
                    : null);
        }

        return null;
    }

    private enum IngestOutcome { Stored, Duplicate, Refused }

    /// <summary>
    /// <paramref name="entry"/> is the sender's roster row as read before the
    /// download. Always present in a private chat; in a group it is null for a
    /// member's first photo, and the row is added in the same write as the image.
    /// </summary>
    private async Task IngestAsync(
        TgMessage message, TgUser sender, TgChat chat,
        Sender? entry, Candidate candidate, CancellationToken ct)
    {
        // Fast-path pre-check: saves a download for the obvious case of a resend.
        // Not authoritative by itself — it reads a Snapshot taken before the download
        // and decode below, both unbounded in duration, so it cannot by itself stop
        // two overlapping deliveries of the same content. The re-check inside
        // MutateAsync further down, under the store's lock, is what actually does.
        if (store.Snapshot.Images.Values.Any(i => i.FileUniqueId == candidate.FileUniqueId))
        {
            await AcknowledgeAsync(message, chat, "Det bildet har jeg allerede.", ct);
            return;
        }

        byte[] original;
        try
        {
            // The whole download happens inside the request: CPU is only allocated
            // while a request is in flight, so there is nowhere to defer this to.
            var path = await telegram.GetFilePathAsync(candidate.FileId, ct);
            original = await telegram.DownloadAsync(path, ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Download failed for file {FileId}.", candidate.FileId);
            await ReplyAsync(chat,
                "Beklager — jeg fikk ikke hentet det bildet. Prøv å sende det på nytt.", ct);
            return;
        }

        ProcessedImage processed;
        try
        {
            processed = ImagePipeline.Process(original);
        }
        catch (ImageTooLargeException e)
        {
            logger.LogWarning("Declined an over-the-decode-limit image from {Sender}.", sender.Id);
            await ReplyAsync(chat, e.Message, ct);
            return;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not decode an image from {Sender}.", sender.Id);
            await ReplyAsync(chat,
                "Beklager — jeg klarte ikke å lese det bildet. Prøv et annet.", ct);
            return;
        }

        // Same fast-path caveat as above: saves the object writes below for the
        // common case, but is still followed by the authoritative re-check.
        if (store.Snapshot.Images.Values.Any(i => i.Sha256 == processed.Sha256))
        {
            await AcknowledgeAsync(message, chat, "Det bildet har jeg allerede.", ct);
            return;
        }

        var id = Ulid.NewUlid().ToString();

        // Objects first, state last: a failure here leaves orphaned bytes, never a
        // manifest entry pointing at nothing. Two overlapping deliveries of the same
        // content can both reach this point (the download and decode above take
        // unbounded time, and — from Task 11 on — the admin upload path is a second
        // writer into the same state); each writes its own set of objects here, and
        // the MutateAsync below re-checks both guards under StateStore's semaphore,
        // so only one of them ends up with a manifest entry. The loser's objects are
        // simply orphaned bytes, which the "objects first" rule already accepts.
        await objects.WriteAsync(ObjectPaths.Original(id, candidate.Extension),
            original, "application/octet-stream", null, ct);
        await objects.WriteAsync(ObjectPaths.Display(id), processed.Display, "image/jpeg", null, ct);
        await objects.WriteAsync(ObjectPaths.Thumb(id), processed.Thumb, "image/jpeg", null, ct);

        var approved = false;
        var outcome = await store.MutateAsync(state =>
        {
            // The authoritative check. This runs while MutateAsync holds its
            // semaphore (and, on a precondition-failure retry, against a freshly
            // reloaded state), so two overlapping calls cannot both see "no
            // duplicate" and both add an entry — one of them always observes the
            // other's write first.
            var isDuplicate = state.Images.Values.Any(i =>
                i.FileUniqueId == candidate.FileUniqueId || i.Sha256 == processed.Sha256);
            if (isDuplicate) return IngestOutcome.Duplicate;

            var now = DateTimeOffset.UtcNow;
            var status = entry?.Status;
            if (chat.IsGroup)
            {
                // Re-read under the lock, unlike the private path's entry: a group
                // member was never asked to join, so an admin removing the group or
                // banning the member during the download must win. A ban that lost
                // this race would leave a photo the ban cascade has already swept past.
                var group = state.Settings.Groups.FirstOrDefault(g => g.Id == chat.Id);
                if (group is not { Listening: true }) return IngestOutcome.Refused;

                var member = state.Settings.Senders.FirstOrDefault(s => s.Id == sender.Id);
                if (member is null)
                {
                    // Posting a photo in a group the bot listens to is this member's
                    // join. The group was told so by the notice when listening started.
                    member = new Sender
                    {
                        Id = sender.Id,
                        Name = sender.DisplayName,
                        Status = SenderStatus.Known,
                        FirstSeen = now,
                    };
                    state.Settings.Senders.Add(member);
                }
                if (member.Status == SenderStatus.Banned) return IngestOutcome.Refused;

                // Free: this write happens anyway, and it keeps a renamed group
                // recognisable on the admin page.
                if (!string.IsNullOrWhiteSpace(chat.Title)) group.Title = chat.Title;
                status = member.Status;
            }

            approved = status == SenderStatus.AutoApprove;
            state.Images[id] = new ImageRecord
            {
                Id = id,
                Source = ImageSource.Telegram,
                SenderId = sender.Id,
                SenderName = sender.DisplayName,
                FileUniqueId = candidate.FileUniqueId,
                Sha256 = processed.Sha256,
                Caption = string.IsNullOrWhiteSpace(message.Caption) ? null : message.Caption,
                Status = approved ? ImageStatus.Approved : ImageStatus.Pending,
                Pin = PinKind.None,
                Width = processed.Width,
                Height = processed.Height,
                ReceivedAt = now,
                DecidedAt = approved ? now : null,
                SortKey = id,
                OriginalExtension = candidate.Extension,
            };
            return IngestOutcome.Stored;
        }, ct);

        if (outcome == IngestOutcome.Refused) return;

        if (outcome == IngestOutcome.Duplicate)
        {
            await AcknowledgeAsync(message, chat, "Det bildet har jeg allerede.", ct);
            return;
        }

        if (chat.IsGroup)
        {
            // Per message, not per album: a reaction sits on the photo it is about,
            // so five of them are no noisier than one.
            await telegram.SetMessageReactionAsync(
                chat.Id, message.MessageId, approved ? LiveReaction : QueuedReaction, ct);
            return;
        }

        await AcknowledgeAsync(message, chat,
            approved ? "Mottatt — det er på skjermen nå." : "Mottatt — en arrangør godkjenner det snart.",
            ct);
    }

    /// <summary>
    /// One reply per send, or one per album rather than one per photo. The
    /// check-and-add is one atomic step under the lock, so two album members
    /// finishing concurrently cannot both observe "not yet acknowledged" and both send.
    /// </summary>
    private async Task AcknowledgeAsync(
        TgMessage message, TgChat chat, string text, CancellationToken ct)
    {
        if (chat.IsGroup) return;

        if (message.MediaGroupId is { } group)
        {
            lock (_groupLock)
            {
                if (!_acknowledgedGroups.Add(group)) return;
            }
        }

        await telegram.SendMessageAsync(chat.Id, text, ct);
    }

    /// <summary>
    /// A text reply to whoever sent the photo — in a private chat. In a group it
    /// would go to every member, so a failure there is left to the log.
    /// </summary>
    private async Task ReplyAsync(TgChat chat, string text, CancellationToken ct)
    {
        if (chat.IsGroup) return;
        await telegram.SendMessageAsync(chat.Id, text, ct);
    }
}
