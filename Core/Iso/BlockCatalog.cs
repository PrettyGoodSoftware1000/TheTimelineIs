using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// One piece of ground art: a whole .png drawn in a single draw, positioned by
/// its anchor and sized by its scale.
///
/// The anchor is the pixel of the image that sits at the exact centre of a grid
/// square's top face — the middle of a surface, or the middle of the surface on
/// top of a block. Because every piece follows that one rule, art of any size
/// or shape lands on the grid the same way, and a piece with a deep body or a
/// fringe of roots simply overhangs its square.
/// </summary>
public class GroundPiece
{
    public string File = "";
    public string Family = "";
    /// <summary>Image pixel that lands at the centre of the square's top face.</summary>
    public Point Anchor;
    /// <summary>1f = one image pixel per virtual pixel.</summary>
    public float Scale = 1f;
    /// <summary>Line in Blocks.txt where this piece starts, for error messages.</summary>
    public int Line;

    public string Path => $"{BlockCatalog.Folder}/{File}";

    /// <summary>The file name as levels refer to it, with or without ".png".</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(File);

    /// <summary>
    /// Where to draw this piece so its anchor lands on <paramref name="topCentre"/>,
    /// the screen position of the square's top-face centre.
    /// </summary>
    public Rectangle RectAt(Vector2 topCentre, int texWidth, int texHeight) => new(
        (int)MathF.Round(topCentre.X - Anchor.X * Scale),
        (int)MathF.Round(topCentre.Y - Anchor.Y * Scale),
        Math.Max(1, (int)MathF.Round(texWidth * Scale)),
        Math.Max(1, (int)MathF.Round(texHeight * Scale)));
}

/// <summary>
/// The ground palette (Content/Images/Blocks/Blocks.txt) and the decoration
/// list (Content/Images/Decorations/Decorations.txt). Index files exist because
/// app bundles can't enumerate directories.
///
/// Pieces are grouped into families of interchangeable art. The editor offers a
/// family, then a piece within it or Random; Random draws only from the family
/// it was asked for, so grass never turns to stone and a block never turns into
/// a surface. Which piece a Random brush chose is settled when the square is
/// painted and written into the level, so a level looks the same every time it
/// loads.
/// </summary>
public static class BlockCatalog
{
    public const string Folder = "Content/Images/Blocks";
    public const string BlocksIndex = "Content/Images/Blocks/Blocks.txt";
    public const string DecorationsIndex = "Content/Images/Decorations/Decorations.txt";

    private static List<GroundPiece>? _pieces;
    private static List<string>? _decorations;

    public static IReadOnlyList<GroundPiece> Pieces => _pieces ??= LoadPieces();

    /// <summary>Family names in the order Blocks.txt declares them.</summary>
    public static IReadOnlyList<string> Families =>
        Pieces.Select(p => p.Family).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static IReadOnlyList<GroundPiece> PiecesIn(string family) =>
        Pieces.Where(p => p.Family.Equals(family, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The piece a level line names. Levels may write "Grass" or "Grass.png";
    /// both find the same piece, so hand-edited files keep working.
    /// </summary>
    public static GroundPiece? Find(string nameOrFile) =>
        Pieces.FirstOrDefault(p =>
            p.File.Equals(nameOrFile, StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals(System.IO.Path.GetFileNameWithoutExtension(nameOrFile),
                StringComparison.OrdinalIgnoreCase));

    public static bool IsPiece(string nameOrFile) => Find(nameOrFile) != null;

    /// <summary>The family a piece belongs to, or "" when the piece is unknown.</summary>
    public static string FamilyOf(string nameOrFile) => Find(nameOrFile)?.Family ?? "";

    public static IReadOnlyList<string> Decorations =>
        _decorations ??= AssetLoader.ReadNumbered(DecorationsIndex, DecorationsIndex)
            .Select(l => l.Text).ToList();

    public static string DecorationPath(string file) => $"Content/Images/Decorations/{file}";

    /// <summary>Tests and the editor re-load content between runs.</summary>
    public static void Reset() { _pieces = null; _decorations = null; }

    /// <summary>
    /// Draws one ground piece with its anchor sitting on <paramref name="topCentre"/>,
    /// the screen position of a square's top-face centre. One draw, at the
    /// piece's own scale: nothing is composited and nothing is stretched to a
    /// height, so a body or a fringe of roots simply overhangs the square.
    ///
    /// Both the level screen and the editor come through here, so the two can't
    /// drift apart about where a piece sits. A piece the catalog doesn't know
    /// draws nothing rather than throwing, so a level naming art that isn't
    /// there still opens in the editor to be fixed.
    /// </summary>
    public static void Draw(SpriteBatch batch, AssetLoader assets, string nameOrFile,
        Vector2 topCentre, Color tint)
    {
        if (Find(nameOrFile) is not GroundPiece piece) return;
        var tex = assets.LoadTexture(piece.Path, out bool found);
        if (!found) return;
        batch.Draw(tex, piece.RectAt(topCentre, tex.Width, tex.Height), tint);
    }

    // ---------------- parsing ----------------

    private static List<GroundPiece> LoadPieces()
    {
        var diag = Diagnostics.Current;
        var pieces = new List<GroundPiece>();
        string family = "";
        GroundPiece? current = null;
        // Anchor/Scale written before a family's first Piece are that family's
        // default, which its pieces inherit unless they say otherwise. Same
        // "most specific wins" rule Config.txt uses for scales.
        Point familyAnchor = Point.Zero;
        float familyScale = 1f;

        foreach (var (lineNo, raw) in AssetLoader.ReadNumbered(BlocksIndex, BlocksIndex))
        {
            string line = TextUtil.Clean(raw);
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                diag.Error(BlocksIndex, lineNo, $"unrecognized line '{line}' — expected 'Key: value'");
                continue;
            }
            string key = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();

            switch (key)
            {
                case "family":
                    if (value.Length == 0)
                        diag.Error(BlocksIndex, lineNo, "'Family:' has no name after it");
                    family = value;
                    current = null;
                    familyAnchor = Point.Zero;
                    familyScale = 1f;
                    break;

                case "piece":
                    if (value.Length == 0)
                    {
                        diag.Error(BlocksIndex, lineNo, "'Piece:' has no file name after it");
                        break;
                    }
                    if (family.Length == 0)
                    {
                        diag.Error(BlocksIndex, lineNo,
                            $"piece '{value}' appears before any 'Family:' line, so nothing can select it");
                        break;
                    }
                    // the same art may stand in for several families while a
                    // set is being built. Harmless — a level records the piece,
                    // not the family — but the eyedropper can only adopt one of
                    // them, so say so.
                    if (pieces.FirstOrDefault(p =>
                            p.File.Equals(value, StringComparison.OrdinalIgnoreCase)) is GroundPiece twin)
                        diag.Warn(BlocksIndex, lineNo,
                            $"'{value}' is also in family '{twin.Family}'. That works, but the " +
                            $"eyedropper will call it '{twin.Family}' wherever it is used.");
                    current = new GroundPiece
                    {
                        File = value, Family = family, Line = lineNo,
                        Anchor = familyAnchor, Scale = familyScale,
                    };
                    pieces.Add(current);
                    break;

                case "anchor":
                    if (ParsePair(value) is not Point anchor)
                        diag.Error(BlocksIndex, lineNo,
                            $"Anchor must be two numbers like '180, 90', got '{value}'");
                    else if (current != null) current.Anchor = anchor;
                    else if (family.Length > 0) familyAnchor = anchor;
                    else diag.Error(BlocksIndex, lineNo,
                        "'Anchor:' appears before any 'Family:' line");
                    break;

                case "scale":
                    if (!float.TryParse(value.TrimEnd('%', ' '), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float pct) || pct <= 0f)
                        diag.Error(BlocksIndex, lineNo,
                            $"Scale must be a percent above 0, got '{value}'");
                    else if (current != null) current.Scale = pct / 100f;
                    else if (family.Length > 0) familyScale = pct / 100f;
                    else diag.Error(BlocksIndex, lineNo,
                        "'Scale:' appears before any 'Family:' line");
                    break;

                default:
                    diag.Warn(BlocksIndex, lineNo, $"unknown line '{line}' ignored");
                    break;
            }
        }

        if (pieces.Count == 0)
            diag.Error(BlocksIndex, 0, "no ground pieces declared, so levels have nothing to draw");
        return pieces;
    }

    private static Point? ParsePair(string value)
    {
        var bits = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return bits.Length == 2 && int.TryParse(bits[0], out int x) && int.TryParse(bits[1], out int y)
            ? new Point(x, y) : null;
    }
}
