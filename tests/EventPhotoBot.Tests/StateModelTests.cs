using System.Text.Json;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class StateModelTests
{
    [Fact]
    public void A_sender_roster_round_trips_through_json()
    {
        var state = new EventState();
        state.Settings.Senders.Add(new Sender
        {
            Id = 42,
            Name = "Guest",
            Status = SenderStatus.AutoApprove,
            FirstSeen = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
        });

        var json = JsonSerializer.SerializeToUtf8Bytes(state, StateJson.Options);
        var round = JsonSerializer.Deserialize<EventState>(json, StateJson.Options)!;

        var sender = Assert.Single(round.Settings.Senders);
        Assert.Equal(42, sender.Id);
        Assert.Equal(SenderStatus.AutoApprove, sender.Status);
        Assert.Contains("\"autoApprove\"", System.Text.Encoding.UTF8.GetString(json));
    }

    [Fact]
    public void A_fresh_state_has_an_empty_roster()
    {
        Assert.Empty(new EventState().Settings.Senders);
    }

    [Fact]
    public void A_state_file_from_before_groups_loads_with_no_groups()
    {
        const string json = """{"images":{},"settings":{"senders":[{"id":42,"name":"Ada","status":"known"}]}}""";

        var state = JsonSerializer.Deserialize<EventState>(json, StateJson.Options)!;

        Assert.NotNull(state.Settings.Groups);
        Assert.Empty(state.Settings.Groups);
        Assert.Single(state.Settings.Senders);
    }

    [Fact]
    public void Groups_round_trip_through_json()
    {
        var state = new EventState();
        state.Settings.Groups.Add(new BotGroup { Id = -1001234, Title = "Festkomiteen", Listening = true });

        var json = JsonSerializer.Serialize(state, StateJson.Options);
        var group = Assert.Single(JsonSerializer.Deserialize<EventState>(json, StateJson.Options)!.Settings.Groups);

        Assert.Equal(-1001234, group.Id);
        Assert.Equal("Festkomiteen", group.Title);
        Assert.True(group.Listening);
    }

    [Fact]
    public void State_round_trips_through_json_with_camel_case_enums()
    {
        var state = new EventState();
        state.Settings.Senders.Add(new Sender { Id = 42, Name = "Ada", Status = SenderStatus.AutoApprove });
        state.Settings.TakeoverImageId = "01ABC";
        state.Images["01ABC"] = new ImageRecord
        {
            Id = "01ABC",
            Source = ImageSource.Telegram,
            Sha256 = "deadbeef",
            Status = ImageStatus.Approved,
            Pin = PinKind.Recurring,
            SortKey = "2026-09-20T18:00:00Z",
            OriginalExtension = "jpg",
        };

        var json = JsonSerializer.Serialize(state, StateJson.Options);
        var back = JsonSerializer.Deserialize<EventState>(json, StateJson.Options)!;

        Assert.Contains("\"telegram\"", json);
        Assert.Contains("\"recurring\"", json);
        Assert.Equal(ImageStatus.Approved, back.Images["01ABC"].Status);
        Assert.Equal("Ada", back.Settings.Senders[0].Name);
        Assert.Equal(SenderStatus.AutoApprove, back.Settings.Senders[0].Status);
        Assert.Equal("01ABC", back.Settings.TakeoverImageId);
    }
}
