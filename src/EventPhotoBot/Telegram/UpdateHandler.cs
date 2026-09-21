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

    private const string JoinPrompt =
        "You are not on the list for this event yet. Scan the QR code on the screen — " +
        "it will send me the code and you can start sending photos straight away.";

    public async Task HandleAsync(TgUpdate update, CancellationToken ct = default)
    {
        var message = update.Message;
        if (message?.From is not { } sender || message.Chat is not { } chat) return;

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
                "Send me photos and they will go up on the screen at the event. " +
                "An organiser approves them first. Everything is deleted after the event.", ct);
            return;
        }

        var candidate = Extract(message);
        if (candidate is null)
        {
            await telegram.SendMessageAsync(chat.Id,
                "Photos only, please — I cannot show videos, stickers or animations.", ct);
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
                "You are in. Send me photos and they will go up on the screen once an " +
                "organiser approves them. Everything is deleted after the event.", ct);
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
                    ? "That photo is too large for me to fetch — Telegram caps bot downloads at 20 MB."
                    : null);
        }

        if (message.Document is { } document)
        {
            if (string.Equals(document.MimeType, "image/heic", StringComparison.OrdinalIgnoreCase))
                return new Candidate(document.FileId, document.FileUniqueId, "heic",
                    "I cannot read HEIC files. Send it as a photo rather than a file, " +
                    "or convert it to JPEG first.");

            if (!ImagePipeline.IsSupportedMimeType(document.MimeType))
                return null;

            var extension = document.MimeType!.Split('/')[^1].ToLowerInvariant();
            return new Candidate(document.FileId, document.FileUniqueId, extension,
                document.FileSize > MaxDownloadBytes
                    ? "That file is too large for me to fetch — Telegram caps bot downloads at 20 MB."
                    : null);
        }

        return null;
    }

    private async Task IngestAsync(
        TgMessage message, TgUser sender, TgChat chat,
        Sender entry, Candidate candidate, CancellationToken ct)
    {
        // Fast-path pre-check: saves a download for the obvious case of a resend.
        // Not authoritative by itself — it reads a Snapshot taken before the download
        // and decode below, both unbounded in duration, so it cannot by itself stop
        // two overlapping deliveries of the same content. The re-check inside
        // MutateAsync further down, under the store's lock, is what actually does.
        if (store.Snapshot.Images.Values.Any(i => i.FileUniqueId == candidate.FileUniqueId))
        {
            await AcknowledgeAsync(message, chat, "I already have that one.", ct);
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
            await telegram.SendMessageAsync(chat.Id,
                "Sorry — I could not fetch that photo. Please send it again.", ct);
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
            await telegram.SendMessageAsync(chat.Id, e.Message, ct);
            return;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not decode an image from {Sender}.", sender.Id);
            await telegram.SendMessageAsync(chat.Id,
                "Sorry — I could not read that image. Please try another.", ct);
            return;
        }

        // Same fast-path caveat as above: saves the object writes below for the
        // common case, but is still followed by the authoritative re-check.
        if (store.Snapshot.Images.Values.Any(i => i.Sha256 == processed.Sha256))
        {
            await AcknowledgeAsync(message, chat, "I already have that one.", ct);
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
        var stored = await store.MutateAsync(state =>
        {
            // The authoritative check. This runs while MutateAsync holds its
            // semaphore (and, on a precondition-failure retry, against a freshly
            // reloaded state), so two overlapping calls cannot both see "no
            // duplicate" and both add an entry — one of them always observes the
            // other's write first.
            var isDuplicate = state.Images.Values.Any(i =>
                i.FileUniqueId == candidate.FileUniqueId || i.Sha256 == processed.Sha256);
            if (isDuplicate) return false;

            approved = entry.Status == SenderStatus.AutoApprove;
            var now = DateTimeOffset.UtcNow;
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
            return true;
        }, ct);

        if (!stored)
        {
            await AcknowledgeAsync(message, chat, "I already have that one.", ct);
            return;
        }

        await AcknowledgeAsync(message, chat,
            approved ? "Got it — it is on the screen now." : "Got it — an organiser will approve it shortly.",
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
        if (message.MediaGroupId is { } group)
        {
            lock (_groupLock)
            {
                if (!_acknowledgedGroups.Add(group)) return;
            }
        }

        await telegram.SendMessageAsync(chat.Id, text, ct);
    }
}
