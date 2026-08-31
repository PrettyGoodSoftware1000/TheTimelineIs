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
/// <summary>
/// A doorway: one square of ground belonging to no room, with rooms either side.
///
/// - Which rooms it joins is NOT stored. It is read off the neighbours, so a
///   door can never name a room it does not actually touch.
/// - Doorway squares touching each other are one wide doorway; no width field.
/// - Opens when anybody stands beside it, and reveals both rooms.
/// </summary>
public class LevelDoor
{
    public int X, Y;
    public bool Open;           // runtime only; never saved

    public Point Tile => new(X, Y);
    public bool Covers(Point p) => p.X == X && p.Y == Y;
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
/// One square of an area transition — the painted pads a party walks onto to
/// move somewhere else in the level.
///
/// A transition is not this one square: it is every painted square touching it,
/// so painting a 2x2 patch makes one pad four squares across. Which squares
/// belong together is worked out from the map rather than stored, so the pads
/// can be reshaped in the editor without any bookkeeping.
///
/// <see cref="Pair"/> is what joins two pads: both ends of the same trip carry
/// the same number. 0 means the pad has not been linked to anything yet and
/// does nothing.
/// </summary>
public class LevelTransition
{
    public int X, Y;
    public int Pair;
}

/// <summary>
/// One isometric level: Content/Levels/{Name}.txt, one entity per line.
///
///   Block: x, y, height, type, room      (room '-' means a doorway square)
///   Decoration: x, y, file
///   Door: x, y
///   Enemy: x, y, name
///   PlayerStart: x, y
///   Trigger: x, y, dialogueName
///   Transition: x, y, pair
///
/// Rooms are labels on blocks. A block with a BLANK room is a doorway square:
/// it belongs to nothing, and the rooms it joins are whichever rooms sit beside
/// it. An unopened door hides the room on the far side, and everything standing
/// in it. The editor writes this file.
/// </summary>
public class LevelData
{
    public string Name = "";
    public Dictionary<Point, LevelBlock> Blocks { get; } = new();
    public List<LevelDecoration> Decorations { get; } = new();
    public List<LevelDoor> Doors { get; } = new();
    public List<LevelEnemy> Enemies { get; } = new();
    public List<LevelTrigger> Triggers { get; } = new();
    public List<LevelTransition> Transitions { get; } = new();
    public List<Point> PlayerStarts { get; } = new();

    public static string PathFor(string name) => $"Content/Levels/{name}.txt";

    public LevelBlock? BlockAt(Point p) => Blocks.TryGetValue(p, out var b) ? b : null;
    public LevelDoor? DoorAt(Point p) => Doors.FirstOrDefault(d => d.Covers(p));
    public LevelDecoration? DecorationAt(Point p) =>
        Decorations.FirstOrDefault(d => d.X == p.X && d.Y == p.Y);
    public LevelTrigger? TriggerAt(Point p) =>
        Triggers.FirstOrDefault(t => t.X == p.X && t.Y == p.Y);
    public LevelTransition? TransitionAt(Point p) =>
        Transitions.FirstOrDefault(t => t.X == p.X && t.Y == p.Y);

    /// <summary>Real rooms. Blank-roomed squares are doorways, not rooms.</summary>
    public IEnumerable<string> RoomNames =>
        Blocks.Values.Select(b => b.Room).Where(r => r.Length > 0)
              .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>What a doorway square carries for a room name: nothing.</summary>
    public const string NoRoom = "";

    /// <summary>
    /// How "no room" is written in the file. A blank field would be swallowed
    /// by the parser's empty-entry trimming and silently read back as "Main",
    /// which would turn every doorway into part of a room on the next load.
    /// </summary>
    public const string NoRoomToken = "-";

    /// <summary>The four squares touching this one.</summary>
    public static IEnumerable<Point> Beside(Point p)
    {
        yield return new Point(p.X + 1, p.Y);
        yield return new Point(p.X - 1, p.Y);
        yield return new Point(p.X, p.Y + 1);
        yield return new Point(p.X, p.Y - 1);
    }

    /// <summary>
    /// The rooms a doorway square opens between: the distinct rooms of the
    /// squares touching it. Two is what a door needs; anything else is a
    /// mistake, and the validator says so.
    /// </summary>
    public List<string> RoomsBeside(Point p) =>
        Beside(p).Select(BlockAt).Where(b => b is { Room.Length: > 0 })
                 .Select(b => b!.Room).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Whether a square is on show right now.
    ///
    /// A doorway belongs to no room, so it cannot be revealed the way a room
    /// is. It shows as soon as EITHER room beside it does, which is what makes
    /// a door visible from both sides rather than only from the one you have
    /// already been in.
    /// </summary>
    public bool Shown(Point p, IReadOnlySet<string> revealedRooms)
    {
        var b = BlockAt(p);
        if (b == null) return false;
        return b.Room.Length > 0
            ? revealedRooms.Contains(b.Room)
            : RoomsBeside(p).Any(revealedRooms.Contains);
    }

    /// <summary>
    /// Every doorway square joined to this one, itself included. Doors touching
    /// each other are one doorway, so a two-square gap opens as a unit without
    /// anybody declaring a width.
    /// </summary>
    public List<LevelDoor> DoorGroup(LevelDoor door)
    {
        var group = new List<LevelDoor> { door };
        var seen = new HashSet<Point> { door.Tile };
        var queue = new Queue<Point>();
        queue.Enqueue(door.Tile);
        while (queue.Count > 0)
            foreach (var n in Beside(queue.Dequeue()))
            {
                if (!seen.Add(n)) continue;
                if (DoorAt(n) is not LevelDoor next) continue;
                group.Add(next);
                queue.Enqueue(n);
            }
        return group;
    }

    public static LevelData Load(string name) =>
        Read(name, PathFor(name), AssetLoader.ReadNumbered(PathFor(name), PathFor(name)));

    /// <summary>
    /// The same parser, over text already in hand rather than a file on disk.
    /// A replay carries its own copy of the level it was played in, so that it
    /// still shows what happened after the level itself has been edited.
    /// </summary>
    public static LevelData FromText(string text, string name, string reportAs)
    {
        var numbered = new List<(int, string)>();
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            numbered.Add((i + 1, line));
        }
        return Read(name, reportAs, numbered);
    }

    private static LevelData Read(string name, string path, List<(int Line, string Text)> source)
    {
        var level = new LevelData { Name = name };
        var diag = Diagnostics.Current;

        foreach (var (lineNo, raw) in source)
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
                        Room = parts.Length < 5 ? "Main"
                            : parts[4] == NoRoomToken ? NoRoom : parts[4],
                    };
                    break;
                case "decoration" when Num(0, out int dx) && Num(1, out int dy) && parts.Length >= 3:
                    level.Decorations.Add(new LevelDecoration { X = dx, Y = dy, File = parts[2] });
                    break;
                // Just a position. Which rooms it joins is worked out from the
                // squares beside it, so there is nothing here to get wrong and
                // nothing that can disagree with the map.
                case "door" when Num(0, out int ox) && Num(1, out int oy):
                    level.Doors.Add(new LevelDoor { X = ox, Y = oy });
                    break;
                case "enemy" when Num(0, out int ex) && Num(1, out int ey) && parts.Length >= 3:
                    level.Enemies.Add(new LevelEnemy { X = ex, Y = ey, Name = parts[2] });
                    break;
                case "trigger" when Num(0, out int tx) && Num(1, out int ty) && parts.Length >= 3:
                    level.Triggers.Add(new LevelTrigger { X = tx, Y = ty, Dialogue = parts[2] });
                    break;
                // the pair number is optional: an unlinked pad is a legal thing
                // to have half-built, and the validator says so rather than the
                // parser refusing the line
                case "transition" when Num(0, out int ax) && Num(1, out int ay):
                {
                    int pair = 0;
                    if (parts.Length >= 3 && !int.TryParse(parts[2], out pair))
                    {
                        diag.Error(path, lineNo,
                            $"transition at {ax},{ay}: the pair number must be a whole number, " +
                            $"got '{parts[2]}'");
                        pair = 0;
                    }
                    level.Transitions.Add(new LevelTransition { X = ax, Y = ay, Pair = pair });
                    break;
                }
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
            new LevelDoor { X = d.X, Y = d.Y, Open = d.Open }));
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
        Transitions.Clear(); Transitions.AddRange(other.Transitions);
        PlayerStarts.Clear(); PlayerStarts.AddRange(other.PlayerStarts);
    }

    /// <summary>Round-trips everything the editor places. Runtime door state is not saved.</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Isometric level. One entity per line; the in-game editor writes this file.");
        sb.AppendLine("# Block: x, y, height(feet), type, room");
        foreach (var b in Blocks.Values.OrderBy(b => b.Y).ThenBy(b => b.X))
            sb.AppendLine($"Block: {b.X}, {b.Y}, {b.Height}, {b.Type}, " +
                (b.Room.Length > 0 ? b.Room : NoRoomToken));
        foreach (var d in Decorations.OrderBy(d => d.Y).ThenBy(d => d.X))
            sb.AppendLine($"Decoration: {d.X}, {d.Y}, {d.File}");
        foreach (var d in Doors.OrderBy(d => d.Y).ThenBy(d => d.X))
            sb.AppendLine($"Door: {d.X}, {d.Y}");
        foreach (var e in Enemies)
            sb.AppendLine($"Enemy: {e.X}, {e.Y}, {e.Name}");
        foreach (var t in Triggers.OrderBy(t => t.Y).ThenBy(t => t.X))
            sb.AppendLine($"Trigger: {t.X}, {t.Y}, {t.Dialogue}");
        foreach (var t in Transitions.OrderBy(t => t.Pair).ThenBy(t => t.Y).ThenBy(t => t.X))
            sb.AppendLine($"Transition: {t.X}, {t.Y}, {t.Pair}");
        foreach (var p in PlayerStarts)
            sb.AppendLine($"PlayerStart: {p.X}, {p.Y}");
        return sb.ToString();
    }

    /// <summary>
    /// The transition squares grouped into pads: every run of squares that
    /// touch each other side-on is one pad. Diagonal contact does NOT join two
    /// pads, so two pads can sit corner to corner without merging into one.
    ///
    /// Worked out from the squares each time rather than stored, so a pad can
    /// be painted, extended and rubbed out in the editor without any ids to
    /// keep straight.
    /// </summary>
    public List<TransitionPad> TransitionPads()
    {
        var left = Transitions.ToDictionary(t => new Point(t.X, t.Y), t => t);
        var pads = new List<TransitionPad>();

        while (left.Count > 0)
        {
            var seed = left.Keys.First();
            var pad = new TransitionPad { Pair = left[seed].Pair };
            var queue = new Queue<Point>();
            queue.Enqueue(seed);
            left.Remove(seed);
            pad.Tiles.Add(seed);

            while (queue.Count > 0)
            {
                var here = queue.Dequeue();
                foreach (var step in new[]
                         {
                             new Point(here.X + 1, here.Y), new Point(here.X - 1, here.Y),
                             new Point(here.X, here.Y + 1), new Point(here.X, here.Y - 1),
                         })
                {
                    if (!left.Remove(step, out var joined)) continue;
                    // a pad wears the pair number of whichever of its squares
                    // carries one, so extending a linked pad keeps the link.
                    // Two DIFFERENT numbers touching is a mistake — one of the
                    // links is about to be lost — so it is remembered and
                    // reported rather than resolved by whichever came first.
                    if (pad.Pair == 0) pad.Pair = joined.Pair;
                    else if (joined.Pair != 0 && joined.Pair != pad.Pair) pad.Mixed = true;
                    pad.Tiles.Add(step);
                    queue.Enqueue(step);
                }
            }
            pads.Add(pad);
        }
        return pads;
    }
}

/// <summary>One area-transition pad: the squares it covers and who it leads to.</summary>
public class TransitionPad
{
    public List<Point> Tiles { get; } = new();

    /// <summary>Both ends of a trip share this. 0 means it leads nowhere yet.</summary>
    public int Pair;

    /// <summary>
    /// True when the squares making up this pad do not agree on their pair
    /// number. Two separately linked pads have been painted into one, so one of
    /// those links is now unreachable. Reported rather than quietly resolved.
    /// </summary>
    public bool Mixed;

    public bool Covers(Point p) => Tiles.Contains(p);

    /// <summary>
    /// A name for this pad that survives being recomputed: its lowest square.
    /// Two pads sharing a Pair need telling apart — the pair alone cannot say
    /// which END of the trip the party is standing on.
    /// </summary>
    public Point Key => Tiles.OrderBy(t => t.Y).ThenBy(t => t.X).First();

    /// <summary>The square a line to this pad should be drawn from — its middle.</summary>
    public Point Center
    {
        get
        {
            int x = 0, y = 0;
            foreach (var t in Tiles) { x += t.X; y += t.Y; }
            return new Point(x / Math.Max(1, Tiles.Count), y / Math.Max(1, Tiles.Count));
        }
    }
}
