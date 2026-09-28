using EventPhotoBot.State;
using EventPhotoBot.Telegram;
using EventPhotoBot.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventPhotoBot.Tests;

public class SwitchButtonTests
{
    private const long Guest = 111;

    private static async Task<(UpdateHandler Handler, StateStore Store, FakeTelegramClient Telegram)> SetupAsync(
        Action<EventState> configure)
    {
        var objects = new InMemoryObjectStore();
        var store = new StateStore(objects);
        await store.LoadAsync();
        await store.MutateAsync(configure);
        var telegram = new FakeTelegramClient();
        return (new UpdateHandler(store, objects, telegram, NullLogger<UpdateHandler>.Instance), store, telegram);
    }

    private static Sender Member(params string[] events) => new()
    {
        Id = Guest, Name = "Guest", CurrentEventId = events[0],
        Memberships = [.. events.Select(e => new Membership { EventId = e })],
    };

    private static TgUpdate Tap(string data, long from = Guest) => new()
    {
        CallbackQuery = new TgCallbackQuery
        {
            Id = "cb1",
            From = new TgUser { Id = from, FirstName = "Guest" },
            Message = new TgMessage { MessageId = 50, Chat = new TgChat { Id = from, Type = "private" } },
            Data = data,
        },
    };

    [Fact]
    public async Task Joining_a_second_event_offers_a_button_back_to_the_first()
    {
        var (handler, _, telegram) = await SetupAsync(s => { s.AddEvent("bryllup", "Bryllup", "wc"); s.Senders.Add(Member("daglig")); });

        await handler.HandleAsync(new TgUpdate
        {
            Message = new TgMessage { From = new TgUser { Id = Guest }, Chat = new TgChat { Id = Guest }, Text = "/start wc" },
        });

        var (_, _, buttons) = Assert.Single(telegram.SentButtons);
        Assert.Equal([new InlineButton("Daglig", "ev:daglig")], buttons);
    }

    [Fact]
    public async Task A_bare_start_offers_buttons_to_other_open_events_even_when_the_current_one_is_closed()
    {
        var (handler, _, telegram) = await SetupAsync(s =>
        {
            s.AddEvent("bryllup", "Bryllup", closed: true);
            s.AddEvent("konsert", "Konsert");
            s.Senders.Add(Member("bryllup", "konsert"));
        });

        await handler.HandleAsync(new TgUpdate
        {
            Message = new TgMessage { From = new TgUser { Id = Guest }, Chat = new TgChat { Id = Guest }, Text = "/start" },
        });

        var (_, text, buttons) = Assert.Single(telegram.SentButtons);
        Assert.Equal("Bryllup er avsluttet.", text);
        Assert.Equal([new InlineButton("Konsert", "ev:konsert")], buttons);
    }

    [Fact]
    public async Task Tapping_a_button_switches_and_edits_the_message()
    {
        var (handler, store, telegram) = await SetupAsync(s => { s.AddEvent("bryllup", "Bryllup"); s.Senders.Add(Member("bryllup", "daglig")); });

        await handler.HandleAsync(Tap("ev:daglig"));

        Assert.Equal("daglig", store.Snapshot.Senders.Single().CurrentEventId);
        var edit = Assert.Single(telegram.Edits);
        Assert.Equal((Guest, 50L, "Du sender nå bilder til Daglig."), (edit.ChatId, edit.MessageId, edit.Text));
        Assert.Equal([new InlineButton("Bryllup", "ev:bryllup")], edit.Buttons);
        Assert.Equal(("cb1", (string?)null), Assert.Single(telegram.Answers));
    }

    [Fact]
    public async Task Tapping_a_closed_event_changes_nothing_and_says_why()
    {
        var (handler, store, telegram) = await SetupAsync(s => { s.AddEvent("bryllup", "Bryllup", closed: true); s.Senders.Add(Member("daglig", "bryllup")); });

        await handler.HandleAsync(Tap("ev:bryllup"));

        Assert.Equal("daglig", store.Snapshot.Senders.Single().CurrentEventId);
        Assert.Empty(telegram.Edits);
        Assert.Equal(("cb1", "Bryllup er avsluttet."), Assert.Single(telegram.Answers));
    }

    [Theory]
    [InlineData("ev:bryllup")]   // not a member
    [InlineData("ev:gone")]      // no such event
    [InlineData("junk")]         // not ours
    public async Task A_tap_that_does_not_apply_is_answered_and_ignored(string data)
    {
        var (handler, store, telegram) = await SetupAsync(s => { s.AddEvent("bryllup", "Bryllup"); s.Senders.Add(Member("daglig")); });

        await handler.HandleAsync(Tap(data));

        Assert.Equal("daglig", store.Snapshot.Senders.Single().CurrentEventId);
        Assert.Empty(telegram.Edits);
        Assert.Single(telegram.Answers);
    }

    [Fact]
    public async Task A_banned_sender_gets_the_spinner_stopped_and_nothing_else()
    {
        var (handler, store, telegram) = await SetupAsync(s =>
        {
            var banned = Member("daglig");
            banned.Banned = true;
            s.Senders.Add(banned);
        });

        await handler.HandleAsync(Tap("ev:daglig"));

        Assert.Equal(("cb1", (string?)null), Assert.Single(telegram.Answers));
        Assert.Empty(telegram.Edits);
    }
}
