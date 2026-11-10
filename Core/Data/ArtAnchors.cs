using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// How far one picture is nudged on its square: X sideways, Y north and south.
/// </summary>
public record ArtAnchor(int X, int Y);

/// <summary>
/// Content/Cast/Anchors.txt: which way a picture is nudged on its square.
///
/// Art comes out of the tool with the figure wherever the artist drew it, and a
/// character drawn a little left of centre stands a little left of centre — on
/// a square that is plain to see. So each one gets an offset, tuned by eye from
/// the ~ menu while looking at the level, and written back here.
///
/// Both numbers move it ACROSS THE GROUND. X is east and west, Y is north and
/// south — positive north, up the screen. Nothing is lifted off the floor:
/// something nudged south is drawn with the ground it has moved onto, so it
/// never ends up under tiles painted later.
///
/// Height is not set here. A picture is hung by its lowest solid pixel, which
/// is a character's feet whatever canvas it came on.
///
///   Werewitch WitchForm: -2
///   Gator Gator: 3, -4
///   VesselGate: 0, 6
///
/// For a character the name is the character and the art folder, which is what
/// tells one form from another: a wolf and a witch are drawn from different
/// pictures and rarely want the same nudge. For an object it is the picture's
/// own name, as Decorations.txt spells it.
/// </summary>
public class ArtAnchors
{
    public const string Path = "Content/Cast/Anchors.txt";

    private readonly Dictionary<string, ArtAnchor> _anchors = new(StringComparer.OrdinalIgnoreCase);

    public static ArtAnchors Current { get; private set; } = new();

    /// <summary>The key one character's art is filed under.</summary>
    public static string KeyFor(string name, string art) =>
        art.Length > 0 ? $"{name} {art}" : name;

    /// <summary>Nothing moved, which is what everybody starts as.</summary>
    public static readonly ArtAnchor None = new(0, 0);

    public ArtAnchor For(string name, string art) => For(KeyFor(name, art));

    /// <summary>The nudge filed under a key — an object's picture name, say.</summary>
    public ArtAnchor For(string key) =>
        _anchors.TryGetValue(key, out var a) ? a : None;

    public void Set(string name, string art, ArtAnchor anchor) =>
        Set(KeyFor(name, art), anchor);

    public void Set(string key, ArtAnchor anchor)
    {
        if (anchor == None) _anchors.Remove(key);
        else _anchors[key] = anchor;
    }

    public static ArtAnchors Load()
    {
        var diag = Diagnostics.Current;
        var lib = new ArtAnchors();

        foreach (var (lineNo, raw) in AssetLoader.ReadNumbered(Path, null))
        {
            string line = TextUtil.Clean(raw);
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                diag.Error(Path, lineNo, $"unrecognized line '{line}' — expected " +
                    "'Name: x' or 'Name: x, y'");
                continue;
            }
            string who = line[..colon].Trim();
            string rest = line[(colon + 1)..].Trim();
            var bits = rest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (bits.Any(b => b.Equals("vertical", StringComparison.OrdinalIgnoreCase)))
            {
                diag.Error(Path, lineNo, $"'{who}': 'vertical' is gone. The second number " +
                    "moves it north and south across the ground now, and always counts — " +
                    "drop the word and keep the numbers.");
                continue;
            }
            if (bits.Length is 0 or > 2 || !int.TryParse(bits[0], out int x) ||
                (bits.Length == 2 && !int.TryParse(bits[1], out _)))
            {
                diag.Error(Path, lineNo, $"'{who}': expected one or two whole numbers of " +
                    $"pixels — east-west, then north-south — got '{rest}'");
                continue;
            }
            lib._anchors[who] = new ArtAnchor(x, bits.Length == 2 ? int.Parse(bits[1]) : 0);
        }
        Current = lib;
        return lib;
    }

    /// <summary>The whole file, ready to write back, with its explanation rebuilt.</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# How far a picture is nudged on its square, in pixels.");
        sb.AppendLine("#   <Name>: east-west");
        sb.AppendLine("#   <Name>: east-west, north-south      (positive north, up the screen)");
        sb.AppendLine("#");
        sb.AppendLine("# Both numbers move it across the ground. Nothing is lifted off the");
        sb.AppendLine("# floor: height comes from the lowest solid pixel of the picture.");
        sb.AppendLine("#");
        sb.AppendLine("# A character is '<Character> <Art folder>'. An object is its picture's");
        sb.AppendLine("# own name.");
        sb.AppendLine("#");
        sb.AppendLine("# Written by the Anchor Art page of the ~ menu.");
        sb.AppendLine();
        foreach (var (key, a) in _anchors.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine(a.Y == 0 ? $"{key}: {a.X}" : $"{key}: {a.X}, {a.Y}");
        return sb.ToString();
    }
}
