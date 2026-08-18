using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;

namespace TheTimelineIs.Core.Iso;

/// <summary>Which half of a checkerboard family a piece belongs to.</summary>
public enum Checker { None, Dark, Light }

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

    /// <summary>
    /// Which half of a checkerboard family this piece belongs to. None for an
    /// ordinary family, where every piece is interchangeable.
    /// </summary>
    public Checker Shade = Checker.None;

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
    /// True when a family declared "Checkerboard Dark:" / "Checkerboard Light:"
    /// headings. Such a family lays its two halves out in a checker: a square
    /// takes a dark piece or a light one purely by its position, so no two
    /// neighbours ever share a shade.
    /// </summary>
    public static bool IsCheckerboard(string family) =>
        PiecesIn(family).Any(p => p.Shade != Checker.None);

    /// <summary>
    /// Which shade belongs on a square. (x + y) alternates across the grid in
    /// both directions at once, which is exactly a checkerboard on an
    /// isometric map as much as on a square one.
    /// </summary>
    public static Checker ShadeAt(Point tile) =>
        ((tile.X + tile.Y) & 1) == 0 ? Checker.Dark : Checker.Light;

    public static IReadOnlyList<GroundPiece> PiecesIn(string family, Checker shade) =>
        PiecesIn(family).Where(p => p.Shade == shade).ToList();

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
        var shade = Checker.None;
        GroundPiece? current = null;

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
                    shade = Checker.None;      // a new family starts plain
                    current = null;
                    break;

                // "Checkerboard Dark:" / "Checkerboard Light:" head the two
                // halves of a checkerboard family. Every Piece after one of
                // them belongs to that half until the next heading or family.
                case "checkerboard dark":
                    shade = Checker.Dark;
                    current = null;
                    break;
                case "checkerboard light":
                    shade = Checker.Light;
                    current = null;
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
                    // One .png may stand in for several families, and often
                    // does while a set is half drawn — one block covering dirt
                    // and stone until their own art exists. It is deliberate
                    // and harmless, since a level records the piece and never
                    // the family, so it is not reported: every diagnostic
                    // raises the startup popup, and a placeholder that lives
                    // for weeks would raise it every single launch.
                    current = new GroundPiece
                        { File = value, Family = family, Line = lineNo, Shade = shade };
                    pieces.Add(current);
                    break;

                // Anchor and Scale belong to the piece above them and to
                // nothing else. Every .png needs its own: pieces differ in size
                // and in how far their art hangs below the square, so there is
                // no such thing as an anchor a family could share.
                case "anchor":
                    if (current == null)
                        diag.Error(BlocksIndex, lineNo,
                            "'Anchor:' appears before any 'Piece:' line, and an anchor " +
                            "belongs to one piece");
                    else if (ParsePair(value) is Point anchor)
                        current.Anchor = anchor;
                    else
                        diag.Error(BlocksIndex, lineNo,
                            $"'{current.File}': Anchor must be two numbers like '180, 90', got '{value}'");
                    break;

                case "scale":
                    if (current == null)
                        diag.Error(BlocksIndex, lineNo, "'Scale:' appears before any 'Piece:' line");
                    else if (float.TryParse(value.TrimEnd('%', ' '), NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out float pct) && pct > 0f)
                        current.Scale = pct / 100f;
                    else
                        diag.Error(BlocksIndex, lineNo,
                            $"'{current.File}': Scale must be a percent above 0, got '{value}'");
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
