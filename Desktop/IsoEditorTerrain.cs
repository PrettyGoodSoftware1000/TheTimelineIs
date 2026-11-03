using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using TheTimelineIs.Core.Iso;

namespace TheTimelineIs.Desktop;

/// <summary>
/// Painting terrain in the map editor: pick grass, paint beside stone, and the
/// edges between them become the right transition tiles as you go.
///
/// THE STATE IS THE CORNERS, NOT THE SQUARES. Which of the sixteen pictures a
/// square shows depends on its four corners, and each corner is shared with
/// the three squares around it. So painting a square means setting its four
/// corners, and that is why the change bleeds one square outwards: the
/// neighbours share those corners and have to be redrawn too.
///
/// WHAT GETS WRITTEN DOWN. The level still stores one finished picture per
/// square — "GrassToStone:6". The corners live only while the editor is open,
/// read back out of the masks when a level loads. Nothing new goes in the
/// file, and a level drawn by hand or imported from PixelLab opens the same
/// way.
///
/// WHERE TWO SETS MEET. A set joins exactly two terrains. Stone-to-grass has
/// nothing to say about wood, so a square whose corners hold both gets a hard
/// edge: it draws solid, in whichever terrain holds most of its corners. Draw
/// a wood-to-stone set later, drop it in, and those same squares start
/// blending with no other change — Bridging() simply starts finding it.
/// </summary>
public partial class IsoEditorScreen
{
    /// <summary>What each corner of the grid is made of, while the editor is open.</summary>
    private readonly Dictionary<Point, string> _corners = new();

    /// <summary>The terrain the brush paints, or "" to use the old piece brush.</summary>
    private string _terrain = "";

    private bool TerrainBrush => _terrain.Length > 0;

    private string TerrainLabel => TerrainBrush ? _terrain : "-";

    /// <summary>
    /// Reads the corner grid back out of the level. Every square that names a
    /// terrain set says what its four corners are, so the picture is enough to
    /// recover the state that drew it.
    ///
    /// Neighbours can disagree only if a file was hand-edited into a shape the
    /// editor would never make. The later square wins and the editor says so,
    /// rather than quietly painting something that does not match the file.
    /// </summary>
    private void RebuildCorners()
    {
        _corners.Clear();
        int clashes = 0;
        foreach (var block in _level.Blocks.Values.OrderBy(b => b.Y).ThenBy(b => b.X))
        {
            if (TerrainCatalog.Resolve(block.Type) is not var (set, mask)) continue;
            var square = new Point(block.X, block.Y);
            foreach (var c in Corners.All)
            {
                var at = Corners.Of(square, c);
                string was = set.TerrainAt(mask, c);
                if (_corners.TryGetValue(at, out var had) &&
                    !had.Equals(was, StringComparison.OrdinalIgnoreCase)) clashes++;
                _corners[at] = was;
            }
        }
        if (clashes > 0)
            Status($"{clashes} corners disagree between squares — the level was edited by hand");
    }

    /// <summary>
    /// Paints one square's four corners, then redraws every square those
    /// corners touch. That is this square and the eight around it: nine
    /// squares, because a corner belongs to four of them.
    /// </summary>
    private void PaintTerrain(Point square)
    {
        foreach (var c in Corners.All) _corners[Corners.Of(square, c)] = _terrain;

        // the square itself must exist; the neighbours are only restyled if
        // they are already there, so painting never grows the map sideways
        if (!_level.Blocks.ContainsKey(square))
            _level.Blocks[square] = new LevelBlock
                { X = square.X, Y = square.Y, Height = _height, Room = _room };
        else
            _level.Blocks[square].Height = _height;

        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                Restyle(new Point(square.X + dx, square.Y + dy));
    }

    /// <summary>
    /// Works out the picture for one square from its corners and writes it in.
    /// Squares that are not there, and squares showing ordinary ground art,
    /// are left alone — a terrain brush never silently converts hand-placed
    /// art it was not asked to touch. The rule itself lives in TerrainCatalog,
    /// so what the editor paints and what a test checks are the same code.
    /// </summary>
    private void Restyle(Point square)
    {
        if (!_level.Blocks.TryGetValue(square, out var block)) return;
        string Corner(Corner c) =>
            _corners.TryGetValue(Corners.Of(square, c), out var t) ? t : "";
        if (TerrainCatalog.TokenForCorners(Corner) is string token) block.Type = token;
    }

    /// <summary>
    /// Rubs a square out of the corner grid as well as the level, so the
    /// neighbours stop blending into ground that is no longer there.
    /// </summary>
    private void ForgetTerrain(Point square)
    {
        foreach (var c in Corners.All)
        {
            var at = Corners.Of(square, c);
            // a corner stays while any remaining square still uses it
            if (!Corners.Touching(at).Any(t =>
                    t.Square != square && _level.Blocks.ContainsKey(t.Square)))
                _corners.Remove(at);
        }
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                Restyle(new Point(square.X + dx, square.Y + dy));
    }

    // ---------------- importing from PixelLab ----------------

    /// <summary>The first entry of the Import menu, when there is nothing to import.</summary>
    private const string NothingToImport = "(put a PixelLab .zip in Content/Imports)";

    /// <summary>Turning the terrain brush off and going back to the piece brush.</summary>
    private const string TerrainOff = "Off (use Ground/Piece)";

    /// <summary>Where exports are dropped to be found: the repo's Content/Imports.</summary>
    private string ImportsDir =>
        System.IO.Path.Combine(System.IO.Directory.GetParent(_levelsDir)!.FullName, "Imports");

    /// <summary>
    /// Every PixelLab export waiting to be imported. A folder you drop zips
    /// into, rather than a file picker: the editor has no windowing toolkit
    /// and a folder works the same on every machine.
    /// </summary>
    private List<string> PixelLabExports()
    {
        try
        {
            if (System.IO.Directory.Exists(ImportsDir))
            {
                var found = System.IO.Directory.GetFiles(ImportsDir, "*.zip")
                    .Concat(System.IO.Directory.GetDirectories(ImportsDir))
                    .Select(p => System.IO.Path.GetFileName(p)!)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (found.Count > 0) return found;
            }
        }
        catch (Exception ex) { Status($"could not read {ImportsDir}: {ex.Message}"); }
        return new List<string> { NothingToImport };
    }

    /// <summary>
    /// Imports one export: its sixteen tiles become a terrain set you can
    /// paint with straight away, and its ground is laid into the level open
    /// right now, on top of whatever is there.
    /// </summary>
    private void ImportExport(string entry)
    {
        if (entry == NothingToImport)
        {
            System.IO.Directory.CreateDirectory(ImportsDir);
            Status($"drop a PixelLab .zip into {ImportsDir}");
            return;
        }

        string repoRoot = System.IO.Directory.GetParent(_levelsDir)!.Parent!.FullName;
        var result = PixelLabImport.Import(
            System.IO.Path.Combine(ImportsDir, entry), repoRoot);
        if (!result.Ok) { Status(result.Message); return; }

        TerrainCatalog.Reset();
        BeginEdit();
        foreach (var b in result.Blocks!) _level.Blocks[new Point(b.X, b.Y)] = b;
        RebuildCorners();
        RecountProblems();

        // ready to paint with what just arrived
        if (TerrainCatalog.Get(result.SetName) is TerrainSet set) _terrain = set.A;
        Status(result.Message.Replace(Environment.NewLine, "  "));
    }
}
