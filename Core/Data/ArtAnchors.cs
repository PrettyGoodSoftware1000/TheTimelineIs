using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TheTimelineIs.Core.Data;

/// <summary>How far one character's art is nudged, and whether height is set by hand.</summary>
public record ArtAnchor(int X, int Y, bool Vertical);

/// <summary>
/// Content/Cast/Anchors.txt: which way a character's picture is nudged on its
/// square.
///
/// Art comes out of the tool with the figure wherever the artist drew it, and a
/// character drawn a little left of centre stands a little left of centre — on
/// a 64-wide square that is plain to see. So each one gets an offset, tuned by
/// eye from the ~ menu while looking at the level, and written back here.
///
/// SIDEWAYS ONLY, unless asked otherwise. Height already works: a character is
/// hung by the lowest solid pixel of its picture, which is its feet whatever
/// canvas it came on. Tick Vertical for the exceptions — something drawn
/// mid-air, or a body whose lowest pixel is a shadow rather than a foot — and
/// the Y offset applies too.
///
///   Werewitch WitchForm: -2
///   Gator Gator: 3, -4, vertical
///
/// The name is the character and the art folder, which is what tells one form
/// from another: a wolf and a witch are drawn from different pictures and
/// rarely want the same nudge.
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
    public static readonly ArtAnchor None = new(0, 0, false);

    public ArtAnchor For(string name, string art) =>
        _anchors.TryGetValue(KeyFor(name, art), out var a) ? a : None;

    public void Set(string name, string art, ArtAnchor anchor)
    {
        string key = KeyFor(name, art);
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
                    "'Character Folder: x' or 'Character Folder: x, y, vertical'");
                continue;
            }
            string who = line[..colon].Trim();
            var bits = line[(colon + 1)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            bool vertical = bits.Any(b => b.Equals("vertical", StringComparison.OrdinalIgnoreCase));
            var numbers = bits.Where(b => !b.Equals("vertical", StringComparison.OrdinalIgnoreCase)).ToList();
            if (numbers.Count is 0 or > 2 || !int.TryParse(numbers[0], out int x) ||
                (numbers.Count == 2 && !int.TryParse(numbers[1], out _)))
            {
                diag.Error(Path, lineNo, $"'{who}': expected one or two whole numbers of pixels, " +
                    $"optionally followed by 'vertical' — got '{line[(colon + 1)..].Trim()}'");
                continue;
            }
            int y = numbers.Count == 2 ? int.Parse(numbers[1]) : 0;
            if (y != 0 && !vertical)
                diag.Warn(Path, lineNo, $"'{who}': has a vertical nudge of {y} but does not say " +
                    "'vertical', so it is ignored — the lowest solid pixel decides the height");
            lib._anchors[who] = new ArtAnchor(x, y, vertical);
        }
        Current = lib;
        return lib;
    }

    /// <summary>The whole file, ready to write back, with its explanation rebuilt.</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# How far a character's art is nudged on its square, in pixels.");
        sb.AppendLine("#   <Character> <Art folder>: x");
        sb.AppendLine("#   <Character> <Art folder>: x, y, vertical");
        sb.AppendLine("#");
        sb.AppendLine("# Sideways only unless the line says 'vertical'. Height normally comes");
        sb.AppendLine("# from the lowest solid pixel of the picture, which is a character's");
        sb.AppendLine("# feet whatever size canvas it was drawn on.");
        sb.AppendLine("#");
        sb.AppendLine("# Written by the Anchor Art page of the ~ menu.");
        sb.AppendLine();
        foreach (var (key, a) in _anchors.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine(a.Vertical ? $"{key}: {a.X}, {a.Y}, vertical" : $"{key}: {a.X}");
        return sb.ToString();
    }
}
