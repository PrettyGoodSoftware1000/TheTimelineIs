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
    /// <summary>
    /// What each corner of the grid is made of, while the editor is open.
    ///
    /// Keyed by HEIGHT as well as position. Squares only blend with
    /// neighbours standing at the same elevation: a step up is a cliff with
    /// its own wall, and there is no art for grass easing over the edge of
    /// one. So raising a square leaves its old neighbours exactly as they
    /// were — they are reading a different height's corners, which this
    /// paint never touched.
    /// </summary>
    private readonly Dictionary<(Point At, int Height), string> _corners = new();

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
                var key = (Corners.Of(square, c), block.Height);
                string was = set.TerrainAt(mask, c);
                if (_corners.TryGetValue(key, out var had) &&
                    !had.Equals(was, StringComparison.OrdinalIgnoreCase)) clashes++;
                _corners[key] = was;
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
        // A block landing in a cube level that already has one MOVES that
        // block, so its old height stops blending; a block landing on a level
        // of its own stacks, and nothing is released because nothing moved.
        var moved = _level.Blocks.AtLevel(square, _height);
        if (moved != null && moved.Height != _height) Release(square, moved.Height);

        foreach (var c in Corners.All) _corners[(Corners.Of(square, c), _height)] = _terrain;

        _level.Blocks.Place(square, new LevelBlock
        {
            X = square.X, Y = square.Y, Height = _height,
            Type = moved?.Type ?? "", Room = moved?.Room ?? _room,
        });

        // Every neighbour is asked to redraw, but each reads the corners at
        // ITS OWN height. One standing higher or lower finds nothing this
        // paint wrote and keeps the blending it already had.
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                Restyle(new Point(square.X + dx, square.Y + dy));
    }

    /// <summary>
    /// Drops a square's corners at one height, keeping any still held by
    /// another square standing at that same height.
    /// </summary>
    private void Release(Point square, int height)
    {
        foreach (var c in Corners.All)
        {
            var at = Corners.Of(square, c);
            bool stillUsed = Corners.Touching(at).Any(t =>
                t.Square != square && _level.Blocks.HasHeight(t.Square, height));
            if (!stillUsed) _corners.Remove((at, height));
        }
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
        // every cube of the column, each reading the corners at its own
        // height: a stack can be grass on top of stone
        foreach (var block in _level.Blocks.Column(square).ToList())
        {
            string Corner(Corner c) =>
                _corners.TryGetValue((Corners.Of(square, c), block.Height), out var t) ? t : "";
            if (TerrainCatalog.TokenForCorners(Corner) is string token) block.Type = token;
        }
    }

    /// <summary>
    /// Rubs a square out of the corner grid as well as the level, so the
    /// neighbours stop blending into ground that is no longer there.
    /// </summary>
    private void ForgetTerrain(Point square, int height)
    {
        Release(square, height);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                Restyle(new Point(square.X + dx, square.Y + dy));
    }

    // ---------------- importing from PixelLab ----------------

    /// <summary>Turning the terrain brush off and going back to the piece brush.</summary>
    private const string TerrainOff = "Off (use Ground/Piece)";

    /// <summary>
    /// The terrain menu, a row at a time.
    ///
    /// One row per SET, holding its two terrains side by side, so the menu
    /// shows at a glance which terrains know how to run into each other —
    /// stone sits next to grass because there is art for that join. A terrain
    /// with nobody to blend with takes the full width of a row on its own,
    /// which is the same thing said the other way.
    ///
    /// A terrain in two sets appears in both rows. That is the point: grass
    /// beside stone and grass beside dirt are two different relationships,
    /// and either row paints the same grass.
    /// </summary>
    private List<string[]> TerrainMenuRows()
    {
        var rows = new List<string[]> { new[] { TerrainOff } };
        foreach (var set in TerrainCatalog.Sets)
            rows.Add(set.A.Equals(set.B, StringComparison.OrdinalIgnoreCase)
                ? new[] { set.A }
                : new[] { set.A, set.B });

        // anything no set mentions — nothing today, but a one-off tile later
        var paired = rows.SelectMany(r => r).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var lone in TerrainCatalog.Terrains.Where(t => !paired.Contains(t)))
            rows.Add(new[] { lone });
        return rows;
    }

    /// <summary>
    /// Where each terrain entry sits. Walks the same rows the menu is built
    /// from, so what is drawn, what is clicked and what is picked can never
    /// disagree about which entry is which.
    /// </summary>
    private List<Rectangle> TerrainMenuRects(Rectangle button)
    {
        int width = Math.Max(button.Width, 460);
        int step = BarH - 12, high = BarH - 14;
        var rects = new List<Rectangle>();
        int y = button.Bottom + 6;
        foreach (var row in TerrainMenuRows())
        {
            int each = row.Length == 1 ? width : (width - 6) / 2;
            for (int i = 0; i < row.Length; i++)
                rects.Add(new Rectangle(button.X + i * (each + 6), y, each, high));
            y += step;
        }
        return rects;
    }

    /// <summary>Where exports are kept when one is imported from elsewhere.</summary>
    private string ImportsDir =>
        System.IO.Path.Combine(System.IO.Directory.GetParent(_levelsDir)!.FullName, "Imports");

    private string RepoRoot =>
        System.IO.Directory.GetParent(_levelsDir)!.Parent!.FullName;

    /// <summary>
    /// Asks for a PixelLab export anywhere on the machine and imports it.
    ///
    /// A real file dialog, borrowed from the desktop — see FilePicker. On a
    /// machine with no dialog to borrow it falls back to the first export
    /// sitting in Content/Imports, so the button still does something.
    /// </summary>
    private void ImportWithPicker()
    {
        string? chosen = FilePicker.Available
            ? FilePicker.PickFile("Open a PixelLab export", "zip", ImportsDir)
            : null;

        if (chosen == null && !FilePicker.Available)
        {
            chosen = FirstWaitingExport();
            if (chosen == null)
            {
                System.IO.Directory.CreateDirectory(ImportsDir);
                Status($"no file dialog on this machine — put a .zip in {ImportsDir}");
                return;
            }
            Status($"no file dialog on this machine — opening {System.IO.Path.GetFileName(chosen)}");
        }
        if (chosen == null) return;       // cancelled, which is not a failure

        ImportExport(chosen);
    }

    /// <summary>The fallback when there is no dialog: whatever is in Content/Imports.</summary>
    private string? FirstWaitingExport()
    {
        try
        {
            if (!System.IO.Directory.Exists(ImportsDir)) return null;
            return System.IO.Directory.GetFiles(ImportsDir, "*.zip")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Imports one export: its sixteen tiles become a terrain set you can
    /// paint with straight away, and its ground is laid into the level open
    /// right now, on top of whatever is there.
    ///
    /// The zip is copied into Content/Imports on the way through, so a set
    /// brought in from the desktop can be re-imported later without going
    /// back to wherever it came from.
    /// </summary>
    private void ImportExport(string zipPath)
    {
        var result = PixelLabImport.Import(zipPath, RepoRoot, _ctx.Game.GraphicsDevice,
            askAboutFringe: AskAboutFringe);
        if (!result.Ok) { Status(result.Message); return; }

        Keep(zipPath);
        TerrainCatalog.Reset();
        BeginEdit();
        foreach (var b in result.Blocks!) _level.Blocks[new Point(b.X, b.Y)] = b;
        RebuildCorners();
        RecountProblems();

        // ready to paint with what just arrived
        if (TerrainCatalog.Get(result.SetName) is TerrainSet set) _terrain = set.A;
        Status(result.Message.Replace(Environment.NewLine, "  "));
    }

    /// <summary>
    /// Asks whether to rub the bright edge pixels off an import.
    ///
    /// The answer is NO unless somebody says otherwise. PixelLab usually
    /// leaves a near-white line down each tile edge, which tiles into white
    /// bars across the map — but "usually" is not "always", and deleting
    /// somebody's pixels without asking is not the importer's call to make.
    /// On a machine with no dialog the tiles come in untouched and the status
    /// line says so.
    /// </summary>
    private bool AskAboutFringe(int pixels)
    {
        if (!FilePicker.Available)
        {
            Status($"{pixels} bright edge pixels left alone — no dialog on this machine to ask with");
            return false;
        }
        return FilePicker.Ask("Imported tiles",
            $"These tiles have {pixels} very bright pixels down their left and right edges. " +
            "Laid out on a grid that usually shows up as white bars between the squares.\n\n" +
            "Make those pixels transparent?");
    }

    /// <summary>Files the export in Content/Imports, unless it already lives there.</summary>
    private void Keep(string zipPath)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ImportsDir);
            string landing = System.IO.Path.Combine(
                ImportsDir, System.IO.Path.GetFileName(zipPath));
            if (!System.IO.Path.GetFullPath(landing).Equals(
                    System.IO.Path.GetFullPath(zipPath), StringComparison.OrdinalIgnoreCase))
                System.IO.File.Copy(zipPath, landing, overwrite: true);
        }
        catch (Exception ex) { Console.WriteLine($"[import] could not keep a copy: {ex.Message}"); }
    }
}
