using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Data;

public abstract record RoomEntry;
public record DialogueEntry(string Speaker, string Text) : RoomEntry { public int Line; }
public record BattleEntry : RoomEntry;

public class Room
{
    public string Background = "";
    public List<string> Cast = new();
    public List<RoomEntry> Entries = new();

    // where those lines live in the file, for error messages
    public int BackgroundLine, CastLine;
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
            Room? room = null;
            foreach (var (lineNo, trimmed) in AssetLoader.ReadNumbered(path, path))
            {
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
                    Diagnostics.Current.Error(path, lineNo,
                        $"line before the first 'Room N:' header: '{trimmed}'");
                    continue;
                }
                if (trimmed.StartsWith("Background:", StringComparison.OrdinalIgnoreCase))
                {
                    room.Background = trimmed["Background:".Length..].Trim();
                    room.BackgroundLine = lineNo;
                    continue;
                }
                if (trimmed.StartsWith("Cast:", StringComparison.OrdinalIgnoreCase))
                {
                    room.Cast = trimmed["Cast:".Length..]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                    room.CastLine = lineNo;
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
                        TextUtil.Clean(trimmed[..colon]), TextUtil.Clean(trimmed[(colon + 1)..]))
                    { Line = lineNo });
                }
                else
                {
                    Diagnostics.Current.Error(path, lineNo,
                        $"unrecognized line '{trimmed}' — expected 'Room N:', 'Background:', " +
                        "'Cast:', '[Battle!]' or 'Speaker: text'");
                }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Current.Error(path, 0, $"could not be read: {ex.Message}");
        }
        return script;
    }
}
