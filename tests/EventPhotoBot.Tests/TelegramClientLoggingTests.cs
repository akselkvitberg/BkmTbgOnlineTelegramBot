using System.Net;
using EventPhotoBot.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EventPhotoBot.Tests;

/// <summary>
/// Regression test for the token-in-logs blocker (B1): HttpClientFactory's built-in
/// logging handlers log the request URI at Information, and Telegram puts the bot
/// token in the URL PATH, which the factory only redacts from the query string. This
/// wires up TelegramClient exactly the way Program.cs does — via AddHttpClient, so the
/// same logging handlers are attached — against a stub inner handler, with a capturing
/// logger provider standing in for Cloud Logging.
///
/// Each test builds the DI container twice: once at the plain "Information" level (no
/// category filter), and once with "System.Net.Http.HttpClient": "Warning" the way
/// appsettings.json now sets it. The first run proves the harness genuinely exercises
/// HttpClientFactory's logging handlers — if it didn't, a passing "no token" assertion
/// would be meaningless. The second is the actual regression check for the fix.
/// </summary>
public class TelegramClientLoggingTests
{
    private const string SyntheticToken = "123456:AAApfvz9SYN7H3TIC-TOKEN-abcdefghijk";

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            HttpResponseMessage response = request.RequestUri!.AbsolutePath.EndsWith("getFile")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"ok":true,"result":{"file_path":"photos/file_1.jpg"}}"""),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"ok":true,"result":true}"""),
                };
            return Task.FromResult(response);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Lines);
        public void Dispose() { }

        private sealed class CapturingLogger(string category, List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (lines) lines.Add($"[{category}] {formatter(state, exception)}");
            }
        }
    }

    private static (ITelegramClient Client, CapturingLoggerProvider Logs) BuildClient(
        LogLevel httpClientCategoryLevel)
    {
        var provider = new CapturingLoggerProvider();
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddProvider(provider);
            builder.SetMinimumLevel(LogLevel.Information);
            // Mirrors the single line changed in appsettings.json for this test's "fixed" run.
            builder.AddFilter("System.Net.Http.HttpClient", httpClientCategoryLevel);
        });

        services.AddSingleton(new AppConfig
        {
            BucketName = "bucket",
            BotToken = SyntheticToken,
            WebhookSecret = "whsecret",
            WebhookPath = "hookpath",
            AdminPassword = "pw",
            CookieSigningKey = "0123456789abcdef0123456789abcdef",
            JoinCode = "party2026",
        });

        services.AddHttpClient<ITelegramClient, TelegramClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler());

        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<ITelegramClient>(), provider);
    }

    public static IEnumerable<object[]> Calls()
    {
        yield return
        [
            (Func<ITelegramClient, Task>)(c => c.GetFilePathAsync("file_1")),
        ];
        yield return
        [
            (Func<ITelegramClient, Task>)(c => c.SendMessageAsync(1, "hello")),
        ];
        yield return
        [
            (Func<ITelegramClient, Task>)(c => c.DownloadAsync("photos/file_1.jpg")),
        ];
        yield return
        [
            (Func<ITelegramClient, Task>)(c => c.SetMessageReactionAsync(-100, 1, "👀")),
        ];
        yield return
        [
            (Func<ITelegramClient, Task>)(c => c.LeaveChatAsync(-100)),
        ];
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task At_the_old_default_level_the_token_leaks_into_the_logs(
        Func<ITelegramClient, Task> call)
    {
        // Proves the harness is real: with no category filter (the state before this
        // fix), HttpClientFactory's logging handlers do write the token-bearing URI.
        var (client, logs) = BuildClient(LogLevel.Information);

        await call(client);

        Assert.Contains(logs.Lines, line => line.Contains(SyntheticToken, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task With_the_configured_filter_the_token_never_reaches_the_logs(
        Func<ITelegramClient, Task> call)
    {
        var (client, logs) = BuildClient(LogLevel.Warning);

        await call(client);

        Assert.DoesNotContain(logs.Lines, line => line.Contains(SyntheticToken, StringComparison.Ordinal));
    }
}
