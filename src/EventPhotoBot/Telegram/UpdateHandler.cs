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
    /// every message is a free way to make the bot talk. Throttles the "scan the QR"
    /// reply to a sender who has not redeemed the join code; in-memory only, so a
    /// redeploy simply resets the cooldown. Redemption itself is never throttled —
    /// see HandleJoinCodeAsync.
    /// </summary>
    private readonly Dictionary<long, DateTimeOffset> _lastUnlistedReplyAt = [];
    private static readonly TimeSpan UnlistedReplyCooldown = TimeSpan.FromSeconds(60);

    private const string JoinPrompt =
        "Du står ikke på listen for dette arrangementet ennå. Skann QR-koden på skjermen — " +
        "den sender koden til meg, og du kan begynne å sende bilder med en gang.";

    public async Task HandleAsync(TgUpdate update, CancellationToken ct = default)
    {
        if (update.CallbackQuery is { } callback)
        {
            await HandleCallbackAsync(callback, ct);
            return;
        }

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

        var entry = store.Snapshot.Senders.FirstOrDefault(s => s.Id == sender.Id);

        // Banned first, and before anything that costs a download, a reply or a
        // write. A banned sender gets no signal at all — a reply would both confirm
        // the ban landed and make the bot a reply relay for whoever earned it.
        if (entry is { Banned: true }) return;

        if (message.Text is { } text && TryReadJoinCode(text, out var supplied))
        {
            await HandleJoinCodeAsync(sender, chat, entry, supplied, ct);
            return;
        }

        if (entry is null || entry.Memberships.Count == 0)
        {
            if (ShouldReplyToUnlisted(sender.Id))
                await telegram.SendMessageAsync(chat.Id, JoinPrompt, ct);
            return;
        }

        if (message.Text is { } command && command.StartsWith("/start", StringComparison.Ordinal))
        {
            await SendCurrentEventAsync(chat, entry, ct);
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

        if (Routing.ResolvePrivateTarget(store.Snapshot, entry, DateTimeOffset.UtcNow) is not { } target)
        {
            // The one event they belong to is over. Throttled like the join prompt: a
            // guest sending twenty photos the day after the wedding gets one answer.
            if (ShouldReplyToUnlisted(sender.Id))
                await telegram.SendMessageAsync(chat.Id, $"{ClosedName(entry)} er avsluttet.", ct);
            return;
        }

        await IngestAsync(message, sender, chat, entry, candidate, target.Id, ct);
    }

    private string ClosedName(Sender entry) =>
        store.Snapshot.Find(entry.CurrentEventId)?.Name ?? "Arrangementet";

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

        // Removed or left. The row goes, routing included: a group the bot is re-added
        // to is unrouted until an organiser routes it again, and the notice goes out then.
        if (store.Snapshot.Groups.All(g => g.Id != chat.Id)) return;
        await store.MutateAsync(state => state.Groups.RemoveAll(g => g.Id == chat.Id), ct);
    }

    /// <summary>
    /// Everything posted in a group. Nothing here ever sends a text reply: a group is
    /// other people's conversation, and a bot that answers in it — a join prompt, a
    /// "received", a decline — is noise for every member, and in a group the bot is
    /// not routed to, a way to make it talk to strangers. The one notice, when an
    /// admin routes the group, is sent from the admin API instead.
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

        var entry = store.Snapshot.Senders.FirstOrDefault(s => s.Id == sender.Id);

        // Banned first, as in a private chat, and before the join code: a banned
        // person must not be able to open a group of their own to the queue.
        if (entry is { Banned: true }) return;

        // Only a group an organiser has routed to an event that is open. Every other
        // group — never routed, routed to an event since deleted or closed — is
        // dropped with no reply and no write: its members did not agree to a screen.
        var group = store.Snapshot.Groups.FirstOrDefault(g => g.Id == chat.Id);
        if (store.Snapshot.Find(group?.EventId) is not { } ev || !ev.IsOpen(DateTimeOffset.UtcNow)) return;

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

        await IngestAsync(message, sender, chat, entry, candidate, ev.Id, ct);
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
        if (store.Snapshot.Groups.All(g => g.Id != from)) return;

        await store.MutateAsync(state =>
        {
            var groups = state.Groups;
            var old = groups.FirstOrDefault(g => g.Id == from);
            if (old is null) return;

            if (groups.FirstOrDefault(g => g.Id == to) is { } existing)
            {
                existing.EventId ??= old.EventId;
                groups.Remove(old);
            }
            else
            {
                old.Id = to;
            }
        }, ct);
    }

    /// <summary>
    /// "/start CODE", from anyone who is not banned. A code for an open event joins it
    /// and makes it where this person's photos go; any other code gets said so. Never
    /// throttled when it matches: being rate-limited out of joining is the worst
    /// possible moment to go quiet on somebody.
    /// </summary>
    private async Task HandleJoinCodeAsync(
        TgUser sender, TgChat chat, Sender? entry, string supplied, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        Event? match = null;
        // Every event's code is compared, not the first match: which event is being
        // joined must not show in how long the answer takes.
        foreach (var ev in store.Snapshot.Events)
            if (SecretComparison.Matches(ev.JoinCode, supplied)) match = ev;

        if (match is null)
        {
            if (entry is { Memberships.Count: > 0 }) await SendCurrentEventAsync(chat, entry, ct);
            else if (ShouldReplyToUnlisted(sender.Id)) await telegram.SendMessageAsync(chat.Id, JoinPrompt, ct);
            return;
        }

        switch (match.PhaseAt(now))
        {
            case EventPhase.Closed:
                await telegram.SendMessageAsync(chat.Id, $"{match.Name} er avsluttet.", ct);
                return;
            case EventPhase.Scheduled:
                await telegram.SendMessageAsync(chat.Id, $"{match.Name} har ikke startet ennå.", ct);
                return;
        }

        var eventId = match.Id;
        var outcome = await store.MutateAsync(state =>
        {
            // Re-checked under the store's lock: two /start messages racing must not
            // produce two rows, and a ban landing meanwhile must win.
            var row = state.Senders.FirstOrDefault(s => s.Id == sender.Id);
            var isNew = row is null;
            if (row is null)
            {
                row = new Sender { Id = sender.Id, Name = sender.DisplayName, FirstSeen = now };
                state.Senders.Add(row);
            }
            if (row.Banned) return (Joined: false, IsNew: false);
            if (row.MembershipIn(eventId) is null) row.Memberships.Add(new Membership { EventId = eventId });
            row.CurrentEventId = eventId;
            return (Joined: true, IsNew: isNew);
        }, ct);
        if (!outcome.Joined) return;

        var buttons = Routing.SwitchButtons(store.Snapshot,
            store.Snapshot.Senders.First(s => s.Id == sender.Id), eventId, now);
        await SendAsync(chat.Id, outcome.IsNew
            ? $"Du er inne. Du sender nå bilder til {match.Name}. En arrangør godkjenner bildene før de vises på skjermen."
            : $"Du sender nå bilder til {match.Name}.", buttons, ct);
    }

    /// <summary>A bare /start from someone already in: where their photos go now.</summary>
    private async Task SendCurrentEventAsync(TgChat chat, Sender entry, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var target = Routing.ResolvePrivateTarget(store.Snapshot, entry, now);
        await SendAsync(chat.Id, target is null
            ? $"{ClosedName(entry)} er avsluttet."
            : $"Du sender bilder til {target.Name}. Send meg bilder, så kommer de opp på skjermen når en arrangør har godkjent dem.",
            Routing.SwitchButtons(store.Snapshot, entry, target?.Id, now), ct);
    }

    /// <summary>Sends with an inline keyboard only when there is one to attach.</summary>
    private Task SendAsync(long chatId, string text, IReadOnlyList<InlineButton> buttons, CancellationToken ct) =>
        buttons.Count == 0
            ? telegram.SendMessageAsync(chatId, text, ct)
            : telegram.SendMessageAsync(chatId, text, buttons, ct);

    /// <summary>
    /// A tap on one of the switch buttons. Always answered, so the button's spinner
    /// stops; only a member tapping an open event changes anything.
    /// </summary>
    private async Task HandleCallbackAsync(TgCallbackQuery callback, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = callback.From is { } from ? store.Snapshot.Senders.FirstOrDefault(s => s.Id == from.Id) : null;
        var data = callback.Data ?? "";

        if (entry is null or { Banned: true } || !data.StartsWith(Routing.CallbackPrefix, StringComparison.Ordinal))
        {
            await telegram.AnswerCallbackQueryAsync(callback.Id, null, ct);
            return;
        }

        var ev = store.Snapshot.Find(data[Routing.CallbackPrefix.Length..]);
        if (ev is null || entry.MembershipIn(ev.Id) is null)
        {
            await telegram.AnswerCallbackQueryAsync(callback.Id, "Det arrangementet er ikke tilgjengelig.", ct);
            return;
        }
        if (!ev.IsOpen(now))
        {
            await telegram.AnswerCallbackQueryAsync(callback.Id, $"{ev.Name} er avsluttet.", ct);
            return;
        }

        var switched = await store.MutateAsync(state =>
        {
            var row = state.Senders.FirstOrDefault(s => s.Id == entry.Id);
            if (row is null || row.Banned || row.MembershipIn(ev.Id) is null) return null;
            row.CurrentEventId = ev.Id;
            return row;
        }, ct);

        await telegram.AnswerCallbackQueryAsync(callback.Id, null, ct);
        if (switched is null || callback.Message?.Chat is not { } chat) return;

        await telegram.EditMessageTextAsync(chat.Id, callback.Message.MessageId,
            $"Du sender nå bilder til {ev.Name}.",
            Routing.SwitchButtons(store.Snapshot, switched, ev.Id, now), ct);
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

    private enum IngestOutcome { Stored, Duplicate, Refused, Closed }

    /// <summary>
    /// <paramref name="entry"/> is the sender's roster row as read before the
    /// download. Always present in a private chat; in a group it is null for a
    /// member's first photo, and the row is added in the same write as the image.
    /// <paramref name="expectedEventId"/> is the event resolved before the download —
    /// the private path's current event, or the group's routed one — used only for
    /// the fast-path duplicate pre-checks below; the authoritative check inside
    /// MutateAsync re-resolves the event under the lock instead of trusting it.
    /// </summary>
    private async Task IngestAsync(
        TgMessage message, TgUser sender, TgChat chat,
        Sender? entry, Candidate candidate, string expectedEventId, CancellationToken ct)
    {
        // Fast-path pre-check: saves a download for the obvious case of a resend.
        // Not authoritative by itself — it reads a Snapshot taken before the download
        // and decode below, both unbounded in duration, so it cannot by itself stop
        // two overlapping deliveries of the same content. The re-check inside
        // MutateAsync further down, under the store's lock, is what actually does.
        if (store.Snapshot.Images.Values.Any(i =>
                i.EventId == expectedEventId && i.FileUniqueId == candidate.FileUniqueId))
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
        if (store.Snapshot.Images.Values.Any(i => i.EventId == expectedEventId && i.Sha256 == processed.Sha256))
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
        var (outcome, storedEventId) = await store.MutateAsync(state =>
        {
            var now = DateTimeOffset.UtcNow;
            string eventId;
            if (chat.IsGroup)
            {
                // Re-read under the lock, unlike the private path's entry: a group
                // member was never asked to join, so an admin removing the group or
                // banning the member during the download must win. A ban that lost
                // this race would leave a photo the ban cascade has already swept past.
                var group = state.Groups.FirstOrDefault(g => g.Id == chat.Id);
                if (group is null) return (IngestOutcome.Refused, "");
                if (state.Find(group.EventId) is not { } routedEvent || !routedEvent.IsOpen(now))
                    return (IngestOutcome.Refused, "");
                var routed = routedEvent.Id;

                var member = state.Senders.FirstOrDefault(s => s.Id == sender.Id);
                if (member is null)
                {
                    // Posting a photo in a group the bot collects from is this member's
                    // join. The group was told so by the notice when collecting started.
                    member = new Sender { Id = sender.Id, Name = sender.DisplayName, FirstSeen = now };
                    state.Senders.Add(member);
                }
                if (member.Banned) return (IngestOutcome.Refused, "");

                var membership = member.MembershipIn(routed);
                if (membership is null)
                {
                    membership = new Membership { EventId = routed };
                    member.Memberships.Add(membership);
                }

                // A group member who also DMs the bot needs somewhere to resolve to;
                // without this, a group-only member's first private photo would find no
                // current event and get the misleading "Arrangementet er avsluttet."
                // Only when null: an existing member's current event is untouched by a
                // group photo — it names where their *private* photos go, and a group
                // post is not that.
                member.CurrentEventId ??= routed;

                // Free: this write happens anyway, and it keeps a renamed group
                // recognisable on the admin page.
                if (!string.IsNullOrWhiteSpace(chat.Title)) group.Title = chat.Title;
                eventId = routed;
                approved = membership.AutoApprove;
            }
            else
            {
                // Re-read under the lock too: the event can close, and the sender be
                // banned, while the download and decode above were running.
                var row = state.Senders.FirstOrDefault(s => s.Id == sender.Id);
                if (row is null || row.Banned) return (IngestOutcome.Refused, "");
                if (Routing.ResolvePrivateTarget(state, row, now) is not { } target) return (IngestOutcome.Closed, "");
                eventId = target.Id;
                approved = row.MembershipIn(eventId)!.AutoApprove;
            }

            // The authoritative duplicate check, scoped to the event just resolved:
            // this runs while MutateAsync holds its semaphore (and, on a
            // precondition-failure retry, against a freshly reloaded state), so two
            // overlapping calls cannot both see "no duplicate" and both add an entry
            // — one of them always observes the other's write first. Scoped rather
            // than global, because the same photo may legitimately go to two events.
            var isDuplicate = state.Images.Values.Any(i =>
                i.EventId == eventId && (i.FileUniqueId == candidate.FileUniqueId || i.Sha256 == processed.Sha256));
            if (isDuplicate) return (IngestOutcome.Duplicate, "");

            state.Images[id] = new ImageRecord
            {
                Id = id,
                EventId = eventId,
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
                TelegramChatId = chat.IsGroup ? chat.Id : null,
                TelegramMessageId = chat.IsGroup ? message.MessageId : null,
            };
            return (IngestOutcome.Stored, eventId);
        }, ct);

        if (outcome == IngestOutcome.Refused) return;
        if (outcome == IngestOutcome.Closed)
        {
            await ReplyAsync(chat, "Arrangementet er avsluttet, så bildet ble ikke lagret.", ct);
            return;
        }

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
                chat.Id, message.MessageId, approved ? Reactions.Live : Reactions.Queued, ct);
            return;
        }

        var name = store.Snapshot.Find(storedEventId)?.Name ?? "";
        await AcknowledgeAsync(message, chat,
            approved ? $"Mottatt til {name} — det er på skjermen nå." : $"Mottatt til {name} — en arrangør godkjenner det snart.",
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
