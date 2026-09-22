using EventPhotoBot.Telegram;

namespace EventPhotoBot.Web;

/// <summary>
/// LOCAL_DEV only. Plays the part of guests talking to the bot: each call builds the
/// update Telegram would have sent and hands it to the real UpdateHandler, so the
/// join code, the roster, the queue and the bot's replies all behave as in production.
/// Mapped after the session gate, like every other page.
/// </summary>
public static class DevEndpoints
{
    public static void MapDev(this WebApplication app)
    {
        app.MapGet("/dev", () => Results.File(
            Path.Combine(app.Environment.ContentRootPath, "DevTools", "dev.html"), "text/html"));

        app.MapPost("/dev/join", async (DevGuest guest, UpdateHandler handler, AppConfig config) =>
        {
            await handler.HandleAsync(Update(guest, message => message.Text = $"/start {config.JoinCode}"));
            return Results.Ok();
        });

        app.MapPost("/dev/message", async (DevGuestText guest, UpdateHandler handler) =>
        {
            await handler.HandleAsync(Update(new DevGuest(guest.Id, guest.Name),
                message => message.Text = guest.Text));
            return Results.Ok();
        });

        app.MapPost("/dev/photo",
            async (HttpRequest http, UpdateHandler handler, OfflineTelegramClient telegram, CancellationToken ct) =>
            {
                var form = await http.ReadFormAsync(ct);
                if (!long.TryParse(form["id"], out var id)) return Results.BadRequest(new { error = "Mangler id." });
                var name = form["name"].ToString();
                var caption = form["caption"].ToString();

                // One media group per request, like an album, so several files get one reply.
                var group = form.Files.Count > 1 ? Guid.NewGuid().ToString("N") : null;
                foreach (var file in form.Files)
                {
                    using var buffer = new MemoryStream();
                    await file.CopyToAsync(buffer, ct);
                    var fileId = telegram.Stage(buffer.ToArray());

                    await handler.HandleAsync(Update(new DevGuest(id, name), message =>
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
    }

    private static long _nextUpdateId;

    private static TgUpdate Update(DevGuest guest, Action<TgMessage> fill)
    {
        var id = Interlocked.Increment(ref _nextUpdateId);
        var message = new TgMessage
        {
            MessageId = id,
            From = new TgUser { Id = guest.Id, FirstName = string.IsNullOrWhiteSpace(guest.Name) ? null : guest.Name },
            Chat = new TgChat { Id = guest.Id },
        };
        fill(message);
        return new TgUpdate { UpdateId = id, Message = message };
    }
}

public sealed record DevGuest(long Id, string? Name);
public sealed record DevGuestText(long Id, string? Name, string Text);
