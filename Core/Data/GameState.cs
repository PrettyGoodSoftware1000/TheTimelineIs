using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// What a save file holds. Progress is recorded on the world map between
/// levels, so there is nothing mid-level to write down: a level either was
/// completed or has to be played again from its start.
/// </summary>
public class SaveData
{
    public List<string> CompletedMissions { get; set; } = new();
    public List<string> Party { get; set; } = new();
}

/// <summary>
/// The whole mutable state of a playthrough: which levels are done, who is in
/// the party, and which level is being played right now.
/// </summary>
public class GameState
{
    public HashSet<string> CompletedMissions = new(StringComparer.OrdinalIgnoreCase);
    public string? CurrentMission;

    /// <summary>The classes chosen at New Game, in pick order.</summary>
    public List<string> Party = new();

    /// <summary>Old saves and dev shortcuts have no party — fall back to one of each class.</summary>
    public List<string> PartyOrDefault() =>
        Party.Count > 0 ? Party : new List<string> { "Dirtbag", "Gun-O-Mancer", "Cyborg", "Werewitch" };

    /// <summary>New Game: wipe everything and set the chosen party.</summary>
    public void Reset(List<string> party)
    {
        CompletedMissions.Clear();
        CurrentMission = null;
        Party = party;
    }

    public void StartMission(string mission) => CurrentMission = mission;

    public void EndMission(bool completed)
    {
        if (completed && CurrentMission != null)
            CompletedMissions.Add(CurrentMission);
        CurrentMission = null;
    }

    public string ToJson() =>
        JsonSerializer.Serialize(
            new SaveData { CompletedMissions = CompletedMissions.ToList(), Party = Party.ToList() },
            new JsonSerializerOptions { WriteIndented = true });

    public void ApplyJson(string json)
    {
        var data = JsonSerializer.Deserialize<SaveData>(json) ?? new SaveData();
        CompletedMissions = new HashSet<string>(data.CompletedMissions, StringComparer.OrdinalIgnoreCase);
        Party = data.Party ?? new List<string>();
        CurrentMission = null;
    }
}
