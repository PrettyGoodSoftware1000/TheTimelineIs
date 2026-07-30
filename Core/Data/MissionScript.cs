using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Data;

public abstract record RoomEntry;
public record DialogueEntry(string Speaker, string Text) : RoomEntry;
public record BattleEntry : RoomEntry;

public class Room
{
    public string Background = "";
    public List<string> Cast = new();
    public List<RoomEntry> Entries = new();
}

/// <summary>
/// Parses a mission file: Content/Missions/{name}/{name}.txt.
/// "Room N:" starts a room; "Background:" and "Cast:" are room headers;
/// "[Battle!]" is a battle marker; "Speaker: text" is dialogue.
/// A room without its own Background inherits the previous room's.
/// </summary>
public class MissionScript
{
    public string Name = "";
    public List<Room> Rooms { get; } = new();

    private static readonly Regex RoomHeader = new(@"^\s*Room\s+\d+\s*:\s*$", RegexOptions.IgnoreCase);

    public static MissionScript Load(string missionName)
    {
        var script = new MissionScript { Name = missionName };
        string path = $"Content/Missions/{missionName}/{missionName}.txt";
        try
        {
            using var stream = TitleContainer.OpenStream(path);
            using var reader = new StreamReader(stream);
            Room? room = null;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                if (RoomHeader.IsMatch(trimmed))
                {
                    room = new Room();
                    if (script.Rooms.Count > 0)
                        room.Background = script.Rooms[^1].Background;
                    script.Rooms.Add(room);
                    continue;
                }
                if (room == null)
                {
                    Console.WriteLine($"[mission {missionName}] line before first room header ignored: {trimmed}");
                    continue;
                }
                if (trimmed.StartsWith("Background:", StringComparison.OrdinalIgnoreCase))
                {
                    room.Background = trimmed["Background:".Length..].Trim();
                    continue;
                }
                if (trimmed.StartsWith("Cast:", StringComparison.OrdinalIgnoreCase))
                {
                    room.Cast = trimmed["Cast:".Length..]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                    continue;
                }
                if (trimmed.Equals("[Battle!]", StringComparison.OrdinalIgnoreCase))
                {
                    room.Entries.Add(new BattleEntry());
                    continue;
                }
                int colon = trimmed.IndexOf(':');
                if (colon > 0)
                {
                    room.Entries.Add(new DialogueEntry(
                        trimmed[..colon].Trim(), trimmed[(colon + 1)..].Trim()));
                }
                else
                {
                    Console.WriteLine($"[mission {missionName}] unrecognized line ignored: {trimmed}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mission {missionName}] failed to load {path}: {ex.Message}");
        }
        return script;
    }
}
