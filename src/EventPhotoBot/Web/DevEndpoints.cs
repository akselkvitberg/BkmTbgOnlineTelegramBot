using EventPhotoBot.State;
using EventPhotoBot.Telegram;

namespace EventPhotoBot.Web;

/// <summary>
/// LOCAL_DEV only. Plays the part of guests talking to the bot: each call builds the
/// update Telegram would have sent and hands it to the real UpdateHandler, so the
/// join code, the roster, the queue and the bot's replies all behave as in production.
/// A guest given a group id posts into that simulated group instead of a private
/// chat with the bot. Mapped after the session gate, like every other page.
/// </summary>
public static class DevEndpoints
{
    public static void MapDev(this WebApplication app)
    {
        app.MapGet("/dev", () => Results.File(
            Path.Combine(app.Environment.ContentRootPath, "DevTools", "dev.html"), "text/html"));

        app.MapPost("/dev/join", async (DevGuest guest, UpdateHandler handler, StateStore store) =>
        {
            var joinCode = (store.Snapshot.Find(guest.EventId) ?? store.Snapshot.Default()).JoinCode;
            await handler.HandleAsync(Update(guest, message => message.Text = $"/start {joinCode}"));
            return Results.Ok();
        });

        app.MapPost("/dev/message", async (DevGuestText guest, UpdateHandler handler) =>
        {
            await handler.HandleAsync(Update(new DevGuest(guest.Id, guest.Name, guest.GroupId, guest.GroupTitle),
                message => message.Text = guest.Text));
            return Results.Ok();
        });

        // Someone adding the bot to the group, or removing it, the way Telegram
        // reports it. Nothing in a simulated group reaches the bot until it is added.
        app.MapPost("/dev/group", async (DevGroup group, UpdateHandler handler) =>
        {
            if (group.Id >= 0) return Results.BadRequest(new { error = "En gruppe-id er negativ." });

            await handler.HandleAsync(new TgUpdate
            {
                UpdateId = Interlocked.Increment(ref _nextUpdateId),
                MyChatMember = new TgChatMemberUpdated
                {
                    Chat = GroupChat(group.Id, group.Title),
                    NewChatMember = new TgChatMember { Status = group.Present ? "member" : "left" },
                },
            });
            return Results.Ok();
        });

        app.MapPost("/dev/photo",
            async (HttpRequest http, UpdateHandler handler, OfflineTelegramClient telegram, CancellationToken ct) =>
            {
                var form = await http.ReadFormAsync(ct);
                if (!long.TryParse(form["id"], out var id)) return Results.BadRequest(new { error = "Mangler id." });
                var name = form["name"].ToString();
                var caption = form["caption"].ToString();
                long? groupId = long.TryParse(form["groupId"], out var parsedGroup) ? parsedGroup : null;
                var guest = new DevGuest(id, name, groupId, form["groupTitle"].ToString());

                // One media group per request, like an album, so several files get one reply.
                var group = form.Files.Count > 1 ? Guid.NewGuid().ToString("N") : null;
                foreach (var file in form.Files)
                {
                    using var buffer = new MemoryStream();
                    await file.CopyToAsync(buffer, ct);
                    var fileId = telegram.Stage(buffer.ToArray());

                    await handler.HandleAsync(Update(guest, message =>
                    {
                        message.Caption = string.IsNullOrWhiteSpace(caption) ? null : caption;
                        message.MediaGroupId = group;
                        message.Photo =
                        [
                            new TgPhotoSize { FileId = fileId, FileUniqueId = fileId, Width = 1, Height = 1 },
                        ];
                    }), ct);
                }
                return Results.Ok();
            }).DisableAntiforgery();

        app.MapGet("/dev/replies", (OfflineTelegramClient telegram) => Results.Ok(telegram.Replies));

        // A guest tapping one of the bot's inline buttons, the way Telegram reports it.
        app.MapPost("/dev/tap", async (DevTap tap, UpdateHandler handler) =>
        {
            await handler.HandleAsync(new TgUpdate
            {
                UpdateId = Interlocked.Increment(ref _nextUpdateId),
                CallbackQuery = new TgCallbackQuery
                {
                    Id = Guid.NewGuid().ToString("N"),
                    From = new TgUser { Id = tap.Id, FirstName = string.IsNullOrWhiteSpace(tap.Name) ? null : tap.Name },
                    Message = new TgMessage { MessageId = tap.MessageId, Chat = new TgChat { Id = tap.Id, Type = "private" } },
                    Data = tap.Data,
                },
            });
            return Results.Ok();
        });
    }

    private static long _nextUpdateId;

    private static TgUpdate Update(DevGuest guest, Action<TgMessage> fill)
    {
        var id = Interlocked.Increment(ref _nextUpdateId);
        var message = new TgMessage
        {
            MessageId = id,
            From = new TgUser { Id = guest.Id, FirstName = string.IsNullOrWhiteSpace(guest.Name) ? null : guest.Name },
            Chat = guest.GroupId is { } group
                ? GroupChat(group, guest.GroupTitle)
                : new TgChat { Id = guest.Id, Type = "private" },
        };
        fill(message);
        return new TgUpdate { UpdateId = id, Message = message };
    }

    private static TgChat GroupChat(long id, string? title) => new()
    {
        Id = id, Type = "supergroup", Title = string.IsNullOrWhiteSpace(title) ? null : title,
    };
}

public sealed record DevGuest(long Id, string? Name, long? GroupId = null, string? GroupTitle = null, string? EventId = null);
public sealed record DevGuestText(long Id, string? Name, string Text, long? GroupId = null, string? GroupTitle = null);
public sealed record DevGroup(long Id, string? Title, bool Present);
public sealed record DevTap(long Id, string? Name, long MessageId, string Data);
