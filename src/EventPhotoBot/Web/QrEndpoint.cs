using EventPhotoBot.State;
using EventPhotoBot.Telegram;
using Microsoft.AspNetCore.Mvc;
using QRCoder;

namespace EventPhotoBot.Web;

public static class QrEndpoint
{
    public static void MapJoinQr(this WebApplication app)
    {
        app.MapGet("/api/join-qr.svg",
            ([FromQuery(Name = "event")] string? eventId, StateStore store, BotIdentity identity) =>
        {
            // Served for a scheduled event too, so its QR can be printed in advance;
            // refused once it is over, when the code only earns a "that has ended".
            if (EventScope.Resolve(store.Snapshot, eventId) is not { } ev
                || ev.PhaseAt(DateTimeOffset.UtcNow) == EventPhase.Closed
                || identity.JoinUrlFor(ev.JoinCode) is not { } url)
                return Results.NotFound();

            // Error correction M: the QR hangs on a wall and may be photographed at an
            // angle or partly glared out. H would be more robust but makes a denser
            // code, which reads worse from the back of a room at this size.
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            var svg = new SvgQRCode(data).GetGraphic(4);

            return Results.Text(svg, "image/svg+xml");
        });
    }
}
