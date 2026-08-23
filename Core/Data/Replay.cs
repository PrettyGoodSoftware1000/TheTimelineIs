using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// What one thing that happened in a mission looked like. Everything a replay
/// holds is one of these, in the order it occurred.
/// </summary>
public enum ReplayEventKind
{
    /// <summary>A new turn began. Who, and their health at the time.</summary>
    Turn,
    /// <summary>Somebody walked. From and To are the ends of the walk.</summary>
    Move,
    /// <summary>A card was played, at a square, against whoever was named.</summary>
    Card,
    /// <summary>Damage or an effect landed on somebody.</summary>
    Hit,
    /// <summary>Somebody died.</summary>
    Down,
    /// <summary>The mission ended: Text says how.</summary>
    End,
}

/// <summary>
/// One line of a replay. Kept deliberately flat — every field is a number or a
/// short string — because the file is meant to be read by a person, and later
/// by something looking for patterns in how a mission was played.
/// </summary>
public class ReplayEvent
{
    public ReplayEventKind Kind;
    public int Turn;
    public string Who = "";
    public string Card = "";
    public string Target = "";
    public Point From, To;
    public int Amount;
    public string Text = "";

    /// <summary>
    /// One line of the file. Key: value pairs on a single line, with only the
    /// fields that mean anything for this kind — a Move line carries squares
    /// and no damage, a Hit line the reverse.
    /// </summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append(Kind).Append(": ").Append(Turn);
        if (Who.Length > 0) sb.Append(" | who ").Append(Who);
        if (Card.Length > 0) sb.Append(" | card ").Append(Card);
        if (Target.Length > 0) sb.Append(" | target ").Append(Target);
        if (Kind is ReplayEventKind.Move)
            sb.Append(" | from ").Append(From.X).Append(',').Append(From.Y)
              .Append(" | to ").Append(To.X).Append(',').Append(To.Y);
        if (Kind is ReplayEventKind.Card)
            sb.Append(" | at ").Append(To.X).Append(',').Append(To.Y);
        if (Amount != 0) sb.Append(" | amount ").Append(Amount);
        if (Text.Length > 0) sb.Append(" | note ").Append(Text.Replace('|', '/'));
        return sb.ToString();
    }

    public static ReplayEvent? Parse(string line)
    {
        int colon = line.IndexOf(':');
        if (colon <= 0) return null;
        if (!Enum.TryParse<ReplayEventKind>(line[..colon].Trim(), true, out var kind)) return null;

        var e = new ReplayEvent { Kind = kind };
        var parts = line[(colon + 1)..].Split('|');
        if (parts.Length > 0 && int.TryParse(parts[0].Trim(), out int turn)) e.Turn = turn;

        foreach (var raw in parts.Skip(1))
        {
            string field = raw.Trim();
            int space = field.IndexOf(' ');
            if (space <= 0) continue;
            string key = field[..space].ToLowerInvariant(), value = field[(space + 1)..].Trim();
            switch (key)
            {
                case "who": e.Who = value; break;
                case "card": e.Card = value; break;
                case "target": e.Target = value; break;
                case "from": e.From = Square(value); break;
                case "to" or "at": e.To = Square(value); break;
                case "amount":
                    if (int.TryParse(value, out int n)) e.Amount = n;
                    break;
                case "note": e.Text = value; break;
            }
        }
        return e;
    }

    private static Point Square(string text)
    {
        var bits = text.Split(',', StringSplitOptions.TrimEntries);
        return bits.Length == 2 &&
               int.TryParse(bits[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) &&
               int.TryParse(bits[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
            ? new Point(x, y) : Point.Zero;
    }
}

/// <summary>
/// The record of a mission: who was in it, where they started, and everything
/// they did, in order.
///
/// It is a plain text file on purpose. The point of keeping these is to be able
/// to look at what a player actually did — by eye now, and later by handing a
/// pile of them to something that can spot the tactics being used and make the
/// enemies answer them. Neither of those is served by a binary blob.
///
/// A replay does NOT record the camera. Where somebody was looking is not part
/// of what happened.
/// </summary>
public class Replay
{
    public string Level = "";
    public string Saved = "";
    public List<string> Party = new();
    public List<ReplayEvent> Events = new();

    /// <summary>How many turns the record covers.</summary>
    public int Turns => Events.Count == 0 ? 0 : Events.Max(e => e.Turn);

    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# A replay of one mission: every turn, in order.");
        sb.AppendLine("# Turn lines open a turn; everything under one belongs to it.");
        sb.AppendLine("# The level this was played in is in the .level.txt beside this file,");
        sb.AppendLine("# copied at the time, so later edits to the level cannot change it.");
        sb.AppendLine($"Level: {Level}");
        sb.AppendLine($"Saved: {Saved}");
        sb.AppendLine($"Party: {string.Join(", ", Party)}");
        sb.AppendLine($"Turns: {Turns}");
        sb.AppendLine();
        foreach (var e in Events) sb.AppendLine(e.Serialize());
        return sb.ToString();
    }

    public static Replay Parse(string text)
    {
        var replay = new Replay();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith("Level:", StringComparison.OrdinalIgnoreCase))
            { replay.Level = line[6..].Trim(); continue; }
            if (line.StartsWith("Saved:", StringComparison.OrdinalIgnoreCase))
            { replay.Saved = line[6..].Trim(); continue; }
            if (line.StartsWith("Party:", StringComparison.OrdinalIgnoreCase))
            {
                replay.Party = line[6..].Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                    StringSplitOptions.TrimEntries).ToList();
                continue;
            }
            if (line.StartsWith("Turns:", StringComparison.OrdinalIgnoreCase)) continue;

            if (ReplayEvent.Parse(line) is ReplayEvent e) replay.Events.Add(e);
        }
        return replay;
    }

    /// <summary>
    /// The events of one turn, in order. Turn 1 is the first. Anything recorded
    /// before a turn opened — the free-move phase before a fight starts — is
    /// turn 0 and plays first.
    /// </summary>
    public List<ReplayEvent> Turn(int turn) => Events.Where(e => e.Turn == turn).ToList();

    /// <summary>A name that sorts by when it was played and cannot collide.</summary>
    public static string NameFor(string level, DateTime when) =>
        $"{level}_{when:yyyy-MM-dd_HHmm}";
}
