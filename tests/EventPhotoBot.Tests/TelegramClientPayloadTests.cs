using System.Net;
using System.Text.Json;
using EventPhotoBot.Telegram;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPhotoBot.Tests;

public class TelegramClientPayloadTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public List<(string Method, JsonElement Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            Calls.Add((request.RequestUri!.AbsolutePath.Split('/')[^1], body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true,"result":true}""") };
        }
    }

    private static (TelegramClient Client, Capture Capture) Build()
    {
        var capture = new Capture();
        var config = new AppConfig
        {
            BucketName = "b", BotToken = "t", WebhookSecret = "s", WebhookPath = "p",
            AdminPassword = "a", CookieSigningKey = "0123456789abcdef0123456789abcdef",
        };
        return (new TelegramClient(new HttpClient(capture), config, NullLogger<TelegramClient>.Instance), capture);
    }

    [Fact]
    public async Task Buttons_go_one_per_row_as_an_inline_keyboard()
    {
        var (client, capture) = Build();

        await client.SendMessageAsync(5, "Hei", [new InlineButton("Daglig", "ev:daglig"), new InlineButton("Bryllup", "ev:bryllup")]);

        var rows = capture.Calls.Single().Body.GetProperty("reply_markup").GetProperty("inline_keyboard");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("ev:bryllup", rows[1][0].GetProperty("callback_data").GetString());
    }

    [Fact]
    public async Task A_callback_answer_and_an_edit_carry_their_ids()
    {
        var (client, capture) = Build();

        await client.AnswerCallbackQueryAsync("cb1", "Bryllup er avsluttet.");
        await client.EditMessageTextAsync(5, 9, "Ny", []);

        Assert.Equal("answerCallbackQuery", capture.Calls[0].Method);
        Assert.Equal("cb1", capture.Calls[0].Body.GetProperty("callback_query_id").GetString());
        Assert.Equal("editMessageText", capture.Calls[1].Method);
        Assert.Equal(9, capture.Calls[1].Body.GetProperty("message_id").GetInt64());
    }
}
