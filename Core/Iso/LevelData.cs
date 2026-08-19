using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using TheTimelineIs.Core.Data;

namespace TheTimelineIs.Core.Iso;

public class LevelBlock
{
    public int X, Y;
    public int Height;          // feet above the base plane
    public string Type = "Grass";
    public string Room = "Main";
}

public class LevelDecoration
{
    public int X, Y;
    public string File = "";    // in Content/Images/Decorations/; blocks its square
}

/// <summary>
/// A doorway joining two rooms. Most are a single square, but a wider one is a
/// run of adjacent squares that open together: Width squares starting at X,Y
/// and running along either the X or the Y axis. The whole run is one door —
/// one click opens all of it, and it blocks all of it while shut.
/// </summary>
public class LevelDoor
{
    public int X, Y;
    public string RoomA = "", RoomB = "";

    /// <summary>Squares in the run: 1 small, 2 medium, 4 large.</summary>
    public int Width = 1;

    /// <summary>True when the run goes along Y instead of X. Meaningless at width 1.</summary>
    public bool AlongY;

    public bool Open;           // runtime only; never saved

    /// <summary>Every square this doorway fills.</summary>
    public IEnumerable<Point> Tiles
    {
        get
        {
            for (int i = 0; i < Math.Max(1, Width); i++)
                yield return AlongY ? new Point(X, Y + i) : new Point(X + i, Y);
        }
    }

    public bool Covers(Point p) => AlongY
        ? p.X == X && p.Y >= Y && p.Y < Y + Math.Max(1, Width)
        : p.Y == Y && p.X >= X && p.X < X + Math.Max(1, Width);
}

public class LevelEnemy
{
    public int X, Y;
    public string Name = "";
}

/// <summary>A painted square that plays a dialogue block the first time anyone steps on it.</summary>
public class LevelTrigger
{
    public int X, Y;
    public string Dialogue = "";
    public bool Fired;          // runtime only; never saved
}

/// <summary>
/// One isometric level: Content/Levels/{Name}.txt, one entity per line.
///
///   Block: x, y, height, type, room
///   Decoration: x, y, file
///   Door: x, y, roomA, roomB[, width[, X|Y]]
///   Enemy: x, y, name
///   PlayerStart: x, y
///   Trigger: x, y, dialogueName
///
/// Rooms are just labels on blocks; a door joins two of them and hides RoomB
/// (and everything standing in it) until opened. The editor writes this file.
/// </summary>
public class LevelData
{
    public string Name = "";
    public Dictionary<Point, LevelBlock> Blocks { get; } = new();
    public List<LevelDecoration> Decorations { get; } = new();
    public List<LevelDoor> Doors { get; } = new();
    public List<LevelEnemy> Enemies { get; } = new();
    public List<LevelTrigger> Triggers { get; } = new();
    public List<Point> PlayerStarts { get; } = new();

    public static string PathFor(string name) => $"Content/Levels/{name}.txt";

    public LevelBlock? BlockAt(Point p) => Blocks.TryGetValue(p, out var b) ? b : null;
    public LevelDoor? DoorAt(Point p) => Doors.FirstOrDefault(d => d.Covers(p));
    public LevelDecoration? DecorationAt(Point p) =>
        Decorations.FirstOrDefault(d => d.X == p.X && d.Y == p.Y);
    public LevelTrigger? TriggerAt(Point p) =>
        Triggers.FirstOrDefault(t => t.X == p.X && t.Y == p.Y);

    public IEnumerable<string> RoomNames =>
        Blocks.Values.Select(b => b.Room).Distinct(StringComparer.OrdinalIgnoreCase);

    public static LevelData Load(string name)
    {
        var level = new LevelData { Name = name };
        string path = PathFor(name);
        var diag = Diagnostics.Current;

        foreach (var (lineNo, raw) in AssetLoader.ReadNumbered(path, path))
        {
            string line = TextUtil.Clean(raw);
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                diag.Error(path, lineNo, $"unrecognized line '{line}'");
                continue;
            }
            string key = line[..colon].Trim().ToLowerInvariant();
            var parts = line[(colon + 1)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            bool Num(int i, out int v) =>
                int.TryParse(i < parts.Length ? parts[i] : "", NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out v);

            switch (key)
            {
                case "block" when Num(0, out int bx) && Num(1, out int by) && Num(2, out int bh) && parts.Length >= 4:
                    level.Blocks[new Point(bx, by)] = new LevelBlock
                    {
                        X = bx, Y = by, Height = Math.Max(0, bh),
                        Type = parts[3],
                        Room = parts.Length >= 5 ? parts[4] : "Main",
                    };
                    break;
                case "decoration" when Num(0, out int dx) && Num(1, out int dy) && parts.Length >= 3:
                    level.Decorations.Add(new LevelDecoration { X = dx, Y = dy, File = parts[2] });
                    break;
                // width and axis are optional so every level written before
                // wide doors existed still reads as a run of one square
                case "door" when Num(0, out int ox) && Num(1, out int oy) && parts.Length >= 4:
                {
                    // A mistyped width or axis is reported rather than quietly
                    // becoming a one-square door pointing the wrong way — a
                    // door in the wrong place is exactly the kind of thing
                    // nobody notices until the level is unfinishable.
                    int width = 1;
                    if (parts.Length >= 5)
                    {
                        if (!int.TryParse(parts[4], out width) || width < 1)
                        {
                            diag.Error(path, lineNo,
                                $"door at {ox},{oy}: width must be a whole number of squares, " +
                                $"got '{parts[4]}'");
                            width = 1;
                        }
                    }
                    bool alongY = false;
                    if (parts.Length >= 6)
                    {
                        string axis = parts[5].Trim();
                        if (axis.Equals("y", StringComparison.OrdinalIgnoreCase)) alongY = true;
                        else if (!axis.Equals("x", StringComparison.OrdinalIgnoreCase))
                            diag.Error(path, lineNo,
                                $"door at {ox},{oy}: the axis must be X or Y, got '{axis}'");
                    }
                    level.Doors.Add(new LevelDoor
                    {
                        X = ox, Y = oy, RoomA = parts[2], RoomB = parts[3],
                        Width = width, AlongY = alongY,
                    });
                    break;
                }
                case "enemy" when Num(0, out int ex) && Num(1, out int ey) && parts.Length >= 3:
                    level.Enemies.Add(new LevelEnemy { X = ex, Y = ey, Name = parts[2] });
                    break;
                case "trigger" when Num(0, out int tx) && Num(1, out int ty) && parts.Length >= 3:
                    level.Triggers.Add(new LevelTrigger { X = tx, Y = ty, Dialogue = parts[2] });
                    break;
                case "playerstart" when Num(0, out int px) && Num(1, out int py):
                    level.PlayerStarts.Add(new Point(px, py));
                    break;
                default:
                    diag.Error(path, lineNo, $"malformed '{key}' line: '{line}'");
                    break;
            }
        }
        return level;
    }

    /// <summary>
    /// A deep copy of everything placeable — what the editor's undo stack keeps.
    /// </summary>
    public LevelData Clone()
    {
        var copy = new LevelData { Name = Name };
        foreach (var (at, b) in Blocks)
            copy.Blocks[at] = new LevelBlock { X = b.X, Y = b.Y, Height = b.Height, Type = b.Type, Room = b.Room };
        copy.Decorations.AddRange(Decorations.Select(d =>
            new LevelDecoration { X = d.X, Y = d.Y, File = d.File }));
        copy.Doors.AddRange(Doors.Select(d =>
            new LevelDoor
            {
                X = d.X, Y = d.Y, RoomA = d.RoomA, RoomB = d.RoomB,
                Width = d.Width, AlongY = d.AlongY, Open = d.Open,
            }));
        copy.Enemies.AddRange(Enemies.Select(e => new LevelEnemy { X = e.X, Y = e.Y, Name = e.Name }));
        copy.Triggers.AddRange(Triggers.Select(t =>
            new LevelTrigger { X = t.X, Y = t.Y, Dialogue = t.Dialogue, Fired = t.Fired }));
        copy.PlayerStarts.AddRange(PlayerStarts);
        return copy;
    }

    /// <summary>Replaces this level's contents with another's — undo, in place.</summary>
    public void CopyFrom(LevelData other)
    {
        Blocks.Clear();
        foreach (var (at, b) in other.Blocks) Blocks[at] = b;
        Decorations.Clear(); Decorations.AddRange(other.Decorations);
        Doors.Clear(); Doors.AddRange(other.Doors);
        Enemies.Clear(); Enemies.AddRange(other.Enemies);
        Triggers.Clear(); Triggers.AddRange(other.Triggers);
        PlayerStarts.Clear(); PlayerStarts.AddRange(other.PlayerStarts);
    }

    /// <summary>Round-trips everything the editor places. Runtime door state is not saved.</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Isometric level. One entity per line; the in-game editor writes this file.");
        sb.AppendLine("# Block: x, y, height(feet), type, room");
        foreach (var b in Blocks.Values.OrderBy(b => b.Y).ThenBy(b => b.X))
            sb.AppendLine($"Block: {b.X}, {b.Y}, {b.Height}, {b.Type}, {b.Room}");
        foreach (var d in Decorations.OrderBy(d => d.Y).ThenBy(d => d.X))
            sb.AppendLine($"Decoration: {d.X}, {d.Y}, {d.File}");
        foreach (var d in Doors)
            sb.AppendLine(d.Width > 1
                ? $"Door: {d.X}, {d.Y}, {d.RoomA}, {d.RoomB}, {d.Width}, {(d.AlongY ? "Y" : "X")}"
                : $"Door: {d.X}, {d.Y}, {d.RoomA}, {d.RoomB}");
        foreach (var e in Enemies)
            sb.AppendLine($"Enemy: {e.X}, {e.Y}, {e.Name}");
        foreach (var t in Triggers.OrderBy(t => t.Y).ThenBy(t => t.X))
            sb.AppendLine($"Trigger: {t.X}, {t.Y}, {t.Dialogue}");
        foreach (var p in PlayerStarts)
            sb.AppendLine($"PlayerStart: {p.X}, {p.Y}");
        return sb.ToString();
    }
}
