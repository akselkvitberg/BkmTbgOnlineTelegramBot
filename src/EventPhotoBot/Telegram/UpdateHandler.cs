using EventPhotoBot.Imaging;
using EventPhotoBot.State;

namespace EventPhotoBot.Telegram;

public sealed class UpdateHandler(
    StateStore store,
    IObjectStore objects,
    ITelegramClient telegram,
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
    /// every message is a free way to make the bot talk. Throttles replies to a
    /// sender who is not on the whitelist, in both pairing mode and normal operation;
    /// in-memory only, so a redeploy simply resets the cooldown.
    /// </summary>
    private readonly Dictionary<long, DateTimeOffset> _lastUnlistedReplyAt = [];
    private static readonly TimeSpan UnlistedReplyCooldown = TimeSpan.FromSeconds(60);

    public async Task HandleAsync(TgUpdate update, CancellationToken ct = default)
    {
        var message = update.Message;
        if (message?.From is not { } sender || message.Chat is not { } chat) return;

        var settings = store.Snapshot.Settings;
        var entry = settings.Whitelist.FirstOrDefault(w => w.Id == sender.Id);

        if (entry is null)
        {
            await HandleUnlistedAsync(sender, chat, settings.PairingMode, ct);
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

    private async Task HandleUnlistedAsync(
        TgUser sender, TgChat chat, bool pairingMode, CancellationToken ct)
    {
        if (!pairingMode)
        {
            if (ShouldReplyToUnlisted(sender.Id))
                await telegram.SendMessageAsync(chat.Id,
                    "You are not on the list for this event, so I cannot accept photos from you.", ct);
            return;
        }

        await store.MutateAsync(state =>
        {
            if (state.Settings.SeenSenders.Any(s => s.Id == sender.Id)) return;
            state.Settings.SeenSenders.Add(new SeenSender
            {
                Id = sender.Id,
                Name = sender.DisplayName,
                FirstSeen = DateTimeOffset.UtcNow,
            });
        });

        if (ShouldReplyToUnlisted(sender.Id))
            await telegram.SendMessageAsync(chat.Id,
                $"Your Telegram id is {sender.Id}. An organiser can add you to the list now — " +
                "try again once they have.", ct);
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
        WhitelistEntry entry, Candidate candidate, CancellationToken ct)
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

            approved = entry.Trusted && state.Settings.AutoApproveTrusted;
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
