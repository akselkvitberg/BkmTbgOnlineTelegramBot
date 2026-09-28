using System.Text.Json;
using EventPhotoBot.State;
using EventPhotoBot.Tests.Fakes;

namespace EventPhotoBot.Tests;

public class StateModelTests
{
    [Fact]
    public void A_sender_roster_round_trips_through_json()
    {
        var state = TestState.New();
        state.Senders.Add(new Sender
        {
            Id = 42, Name = "Guest", Banned = false, CurrentEventId = "daglig",
            FirstSeen = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
            Memberships = [new Membership { EventId = "daglig", AutoApprove = true }],
        });

        var json = JsonSerializer.SerializeToUtf8Bytes(state, StateJson.Options);
        var sender = Assert.Single(JsonSerializer.Deserialize<EventState>(json, StateJson.Options)!.Senders);

        Assert.Equal(42, sender.Id);
        Assert.Equal("daglig", sender.CurrentEventId);
        Assert.True(Assert.Single(sender.Memberships).AutoApprove);
    }

    [Fact]
    public void Groups_round_trip_through_json()
    {
        var state = TestState.New();
        state.Groups.Add(new BotGroup { Id = -1001234, Title = "Festkomiteen", EventId = "daglig" });

        var json = JsonSerializer.Serialize(state, StateJson.Options);
        var group = Assert.Single(JsonSerializer.Deserialize<EventState>(json, StateJson.Options)!.Groups);

        Assert.Equal(-1001234, group.Id);
        Assert.Equal("daglig", group.EventId);
    }

    [Fact]
    public void State_round_trips_through_json_with_camel_case_enums()
    {
        var state = TestState.New();
        state.Default().Settings.TakeoverImageId = "01ABC";
        state.Images["01ABC"] = new ImageRecord
        {
            Id = "01ABC", EventId = "daglig", Source = ImageSource.Telegram, Sha256 = "deadbeef",
            Status = ImageStatus.Approved, Pin = PinKind.Recurring,
            SortKey = "2026-09-20T18:00:00Z", OriginalExtension = "jpg",
        };

        var json = JsonSerializer.Serialize(state, StateJson.Options);
        var back = JsonSerializer.Deserialize<EventState>(json, StateJson.Options)!;

        Assert.Contains("\"telegram\"", json);
        Assert.Contains("\"recurring\"", json);
        Assert.Equal(ImageStatus.Approved, back.Images["01ABC"].Status);
        Assert.Equal("daglig", back.Images["01ABC"].EventId);
        Assert.Equal("01ABC", back.Default().Settings.TakeoverImageId);
    }
}
