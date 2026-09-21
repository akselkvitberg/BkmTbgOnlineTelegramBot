using System.Text.Json;
using EventPhotoBot.State;

namespace EventPhotoBot.Tests;

public class StateModelTests
{
    [Fact]
    public void State_round_trips_through_json_with_camel_case_enums()
    {
        var state = new EventState();
        state.Settings.Whitelist.Add(new WhitelistEntry { Id = 42, Name = "Ada", Trusted = true });
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
        Assert.Equal("Ada", back.Settings.Whitelist[0].Name);
        Assert.True(back.Settings.Whitelist[0].Trusted);
        Assert.Equal("01ABC", back.Settings.TakeoverImageId);
    }

    [Fact]
    public void Fresh_state_has_an_empty_whitelist_and_pairing_off()
    {
        var state = new EventState();
        Assert.Empty(state.Settings.Whitelist);
        Assert.False(state.Settings.PairingMode);
    }
}
