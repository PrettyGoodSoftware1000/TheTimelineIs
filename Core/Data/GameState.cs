using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TheTimelineIs.Core.Data;

public class SavedInstance
{
    public string Name { get; set; } = "";
    public int OccurrenceIndex { get; set; }
    public string SpriteFile { get; set; } = "";
    public bool IsPlayer { get; set; }
    public bool Alive { get; set; } = true;
}

public class SaveData
{
    /// <summary>"map" or "room".</summary>
    public string Location { get; set; } = "map";
    public string? Mission { get; set; }
    public int RoomIndex { get; set; }
    public List<string> CompletedMissions { get; set; } = new();
    /// <summary>Cast state as it was on room entry — a room save replays the room.</summary>
    public List<SavedInstance> Instances { get; set; } = new();
}

/// <summary>
/// The whole mutable state of a playthrough: which missions are done, and —
/// mid-mission — which room we're in and who is on stage (and still alive).
/// </summary>
public class GameState
{
    public HashSet<string> CompletedMissions = new(StringComparer.OrdinalIgnoreCase);
    public string? CurrentMission;
    public int RoomIndex;
    public List<CharacterInstance> Instances = new();

    /// <summary>Instances as they were when the current room began (for room saves).</summary>
    public List<CharacterInstance> RoomEntrySnapshot = new();

    public void StartMission(string mission)
    {
        CurrentMission = mission;
        RoomIndex = 0;
        Instances.Clear();
        RoomEntrySnapshot.Clear();
    }

    public void EndMission(bool completed)
    {
        if (completed && CurrentMission != null)
            CompletedMissions.Add(CurrentMission);
        CurrentMission = null;
        RoomIndex = 0;
        Instances.Clear();
        RoomEntrySnapshot.Clear();
    }

    public void TakeRoomSnapshot() =>
        RoomEntrySnapshot = Instances.Select(i => i.Clone()).ToList();

    public string ToJson(string location)
    {
        var data = new SaveData
        {
            Location = location,
            Mission = location == "room" ? CurrentMission : null,
            RoomIndex = location == "room" ? RoomIndex : 0,
            CompletedMissions = CompletedMissions.ToList(),
            Instances = (location == "room" ? RoomEntrySnapshot : new List<CharacterInstance>())
                .Select(i => new SavedInstance
                {
                    Name = i.Name,
                    OccurrenceIndex = i.OccurrenceIndex,
                    SpriteFile = i.SpriteFile,
                    IsPlayer = i.IsPlayer,
                    Alive = i.Alive,
                }).ToList(),
        };
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <returns>The parsed save, so the caller can route to map or room.</returns>
    public SaveData ApplyJson(string json)
    {
        var data = JsonSerializer.Deserialize<SaveData>(json) ?? new SaveData();
        CompletedMissions = new HashSet<string>(data.CompletedMissions, StringComparer.OrdinalIgnoreCase);
        if (data.Location == "room" && data.Mission != null)
        {
            CurrentMission = data.Mission;
            RoomIndex = data.RoomIndex;
            Instances = data.Instances.Select(s => new CharacterInstance
            {
                Name = s.Name,
                OccurrenceIndex = s.OccurrenceIndex,
                SpriteFile = s.SpriteFile,
                IsPlayer = s.IsPlayer,
                Alive = s.Alive,
            }).ToList();
        }
        else
        {
            CurrentMission = null;
            RoomIndex = 0;
            Instances.Clear();
        }
        RoomEntrySnapshot.Clear();
        return data;
    }
}
