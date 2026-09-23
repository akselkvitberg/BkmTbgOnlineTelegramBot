using System.Net.Http.Json;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class ReactionTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _factory;
    public ReactionTests(AppFactory factory) => _factory = factory;

    private static long _nextMessage = 1000;

    private async Task<(string Id, long MessageId)> SeedGroupPhotoAsync(ImageStatus status = ImageStatus.Pending, long? chat = -4000)
    {
        var id = Guid.NewGuid().ToString("N");
        var messageId = Interlocked.Increment(ref _nextMessage);
        await _factory.Store.MutateAsync(s => s.Images[id] = new ImageRecord
        {
            Id = id, EventId = StateMigration.DefaultEventId, Sha256 = id, SortKey = id, Status = status,
            OriginalExtension = "jpg", SenderId = 4242, TelegramChatId = chat, TelegramMessageId = chat is null ? null : messageId,
        });
        return (id, messageId);
    }

    private (long, long, string?) LastReactionOn(long messageId) =>
        _factory.Telegram.Reactions.Last(r => r.MessageId == messageId);

    [Theory]
    [InlineData("approved", "🔥")]
    [InlineData("rejected", null)]
    [InlineData("hidden", null)]
    public async Task A_decision_sets_the_matching_reaction(string status, string? emoji)
    {
        var (id, message) = await SeedGroupPhotoAsync();

        await _factory.CreateAuthenticatedClient().PostAsJsonAsync($"/api/images/{id}/status", new { status });

        Assert.Equal((-4000L, message, emoji), LastReactionOn(message));
    }

    [Fact]
    public async Task Deleting_a_group_photo_clears_its_reaction()
    {
        var (id, message) = await SeedGroupPhotoAsync(ImageStatus.Approved);

        await _factory.CreateAuthenticatedClient().DeleteAsync($"/api/images/{id}");

        Assert.Equal((-4000L, message, (string?)null), LastReactionOn(message));
    }

    [Fact]
    public async Task A_takeover_that_approves_a_photo_sets_fire()
    {
        var (id, message) = await SeedGroupPhotoAsync();

        await _factory.CreateAuthenticatedClient().PutAsJsonAsync("/api/takeover", new { imageId = id, minutes = 5 });

        Assert.Equal((-4000L, message, "🔥"), LastReactionOn(message));
    }

    [Fact]
    public async Task A_ban_clears_the_reactions_on_everything_they_posted()
    {
        var (_, message) = await SeedGroupPhotoAsync(ImageStatus.Approved);

        await _factory.CreateAuthenticatedClient().PostAsJsonAsync("/api/senders/4242/status", new { status = "banned" });

        Assert.Equal((-4000L, message, (string?)null), LastReactionOn(message));
    }

    [Fact]
    public async Task A_photo_not_from_a_group_gets_no_reaction_call()
    {
        var (id, _) = await SeedGroupPhotoAsync(chat: null);
        var before = _factory.Telegram.Reactions.Count;

        await _factory.CreateAuthenticatedClient().PostAsJsonAsync($"/api/images/{id}/status", new { status = "approved" });

        Assert.Equal(before, _factory.Telegram.Reactions.Count);
    }
}
