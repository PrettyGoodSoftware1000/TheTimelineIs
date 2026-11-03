using System;
using System.Collections.Generic;
using System.Linq;
using TheTimelineIs.Core.Data;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// Content/Images/Terrain/Terrain.txt: every terrain set the game knows.
///
/// An index file rather than a folder listing, for the same reason Blocks.txt
/// is one — an app bundle and a browser cannot look inside a folder. The map
/// editor's importer adds a block here when it unpacks a PixelLab export.
///
///     Set: StoneBrickToGrass
///     A: Stone brick
///     B: grass
///     Tile: 96 x 48
///     Wall: 48
///
/// The sixteen pictures sit in a folder of the set's name, called mask-00.png
/// to mask-15.png. Nothing names them one by one: the mask IS the file name,
/// so a missing one is a missing number and says so.
/// </summary>
public static class TerrainCatalog
{
    public const string Folder = "Content/Images/Terrain";
    public const string Index = "Content/Images/Terrain/Terrain.txt";

    /// <summary>Separates the set from the mask in a level, as in "StoneToGrass:6".</summary>
    public const char MaskSeparator = ':';

    private static List<TerrainSet>? _sets;

    public static IReadOnlyList<TerrainSet> Sets => _sets ??= Load();

    public static void Reset() => _sets = null;

    public static TerrainSet? Get(string name) =>
        Sets.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every terrain any set mentions, each once, in the order sets declare them.</summary>
    public static IReadOnlyList<string> Terrains =>
        Sets.SelectMany(s => new[] { s.A, s.B })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The set that blends these two terrains, or null when nobody has drawn
    /// one. Null is the normal answer for most pairs and means a hard edge —
    /// not an error. Draw the set later and this starts finding it.
    /// </summary>
    public static TerrainSet? Bridging(string one, string other) =>
        Sets.FirstOrDefault(s => s.Joins(one, other));

    /// <summary>Any set that can draw this terrain solid, for painting into bare ground.</summary>
    public static TerrainSet? Holding(string terrain) =>
        Sets.FirstOrDefault(s => s.SideOf(terrain) >= 0);

    /// <summary>
    /// The square four corners should draw — set and mask, as a level writes
    /// it — or null when no set can draw any of them.
    ///
    /// One terrain on all four corners is solid ground. Two is a join, and it
    /// blends when somebody has drawn a set for that pair. Three or more
    /// cannot be drawn by any single tile, so the two commonest are tried and
    /// the rest fall through.
    ///
    /// Falling through is a HARD EDGE, not a failure: the square draws solid
    /// in whichever terrain holds most of its corners, and the join is simply
    /// abrupt. Draw the missing set later and the same corners start blending,
    /// because Bridging starts finding it.
    /// </summary>
    public static string? TokenForCorners(Func<Corner, string> corner)
    {
        var held = Corners.All.Select(corner).ToList();
        if (held.Any(string.IsNullOrEmpty)) return null;

        var ranked = held
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();

        if (ranked.Count >= 2 && Bridging(ranked[0], ranked[1]) is TerrainSet bridge)
            return bridge.TokenFor(Corners.MaskFrom(bridge, corner));

        return Holding(ranked[0]) is TerrainSet solid
            ? solid.TokenFor(solid.SolidMask(ranked[0]))
            : null;
    }

    /// <summary>
    /// Splits what a level square writes — "StoneToGrass:6" — into its set and
    /// its mask. False when the square names ordinary ground art instead, which
    /// is not an error here: the caller falls back to the block catalog.
    /// </summary>
    public static bool TrySplit(string token, out string setName, out int mask)
    {
        setName = ""; mask = 0;
        int at = token.LastIndexOf(MaskSeparator);
        if (at <= 0 || at == token.Length - 1) return false;
        if (!int.TryParse(token[(at + 1)..], out mask) || mask is < 0 or > 15) return false;
        setName = token[..at];
        return true;
    }

    /// <summary>The set and mask a square names, or null when it names something else.</summary>
    public static (TerrainSet Set, int Mask)? Resolve(string token) =>
        TrySplit(token, out string name, out int mask) && Get(name) is TerrainSet set
            ? (set, mask) : null;

    // ---------------- parsing ----------------

    private static List<TerrainSet> Load()
    {
        var diag = Diagnostics.Current;
        var sets = new List<TerrainSet>();
        TerrainSet? current = null;

        foreach (var (lineNo, raw) in AssetLoader.ReadNumbered(Index, null))
        {
            string line = TextUtil.Clean(raw);
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                diag.Error(Index, lineNo, $"unrecognized line '{line}' — expected 'Key: value'");
                continue;
            }
            string key = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();

            if (key == "set")
            {
                if (value.Length == 0) { diag.Error(Index, lineNo, "'Set:' has no name"); continue; }
                if (value.Contains(TerrainCatalog.MaskSeparator))
                {
                    diag.Error(Index, lineNo,
                        $"set name '{value}' cannot contain '{MaskSeparator}' — that is what " +
                        "separates the set from the mask on a level's square");
                    continue;
                }
                if (sets.Any(s => s.Name.Equals(value, StringComparison.OrdinalIgnoreCase)))
                {
                    diag.Warn(Index, lineNo, $"set '{value}' is declared twice");
                    current = sets.First(s => s.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
                    continue;
                }
                current = new TerrainSet { Name = value, Line = lineNo };
                sets.Add(current);
                continue;
            }
            if (current == null)
            {
                diag.Error(Index, lineNo, $"'{key}' appears before any 'Set:' line");
                continue;
            }
            switch (key)
            {
                case "a": current.A = value; break;
                case "b": current.B = value; break;
                case "tile":
                    // "96 x 48" — the top face, not the whole picture
                    var span = value.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (span.Length == 2 && int.TryParse(span[0], out int tw) && tw > 0
                        && int.TryParse(span[1], out int th) && th > 0)
                    { current.TileW = tw; current.TileH = th; }
                    else
                        diag.Error(Index, lineNo,
                            $"'{current.Name}': Tile must be the top face's size like '96 x 48', got '{value}'");
                    break;
                case "wall":
                    if (int.TryParse(value, out int wall) && wall >= 0) current.Wall = wall;
                    else diag.Error(Index, lineNo,
                        $"'{current.Name}': Wall must be how many pixels the sides hang " +
                        $"below the top face, got '{value}'");
                    break;
                default:
                    diag.Warn(Index, lineNo, $"'{current.Name}': unknown line '{line}' ignored");
                    break;
            }
        }

        foreach (var s in sets) Check(s, diag);
        return sets;
    }

    /// <summary>
    /// A set that will not draw says so at startup rather than leaving holes in
    /// a level. The grid's own size is checked too: a set drawn for a different
    /// shaped diamond cannot sit on this grid, and silently misaligned ground
    /// is far harder to work out than a line of text.
    /// </summary>
    private static void Check(TerrainSet s, Diagnostics diag)
    {
        if (s.A.Length == 0 || s.B.Length == 0)
            diag.Error(Index, s.Line,
                $"'{s.Name}' needs an 'A:' and a 'B:' line naming the two terrains it joins");
        else if (s.A.Equals(s.B, StringComparison.OrdinalIgnoreCase))
            diag.Error(Index, s.Line, $"'{s.Name}' joins '{s.A}' to itself");

        if (s.TileW != IsoMath.TileW || s.TileH != IsoMath.TileH)
            diag.Error(Index, s.Line,
                $"'{s.Name}' is drawn for a {s.TileW}x{s.TileH} square but this game's squares are " +
                $"{IsoMath.TileW}x{IsoMath.TileH} — re-export it at that size");

        var missing = Enumerable.Range(0, 16)
            .Where(m => !AssetLoader.Exists($"{Folder}/{s.FileFor(m)}"))
            .ToList();
        if (missing.Count == 16)
            diag.Error(Index, s.Line, $"'{s.Name}' has none of its sixteen pictures — " +
                $"expected {Folder}/{s.Name}/mask-00.png to mask-15.png");
        else if (missing.Count > 0)
            diag.Error(Index, s.Line, $"'{s.Name}' is missing " +
                string.Join(", ", missing.Select(m => $"mask-{m:00}.png")));
    }
}
