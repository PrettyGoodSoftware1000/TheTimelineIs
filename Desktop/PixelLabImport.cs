using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Iso;

namespace TheTimelineIs.Desktop;

/// <summary>
/// What an import produced, or why it didn't.
///
/// <paramref name="Wrote"/> is every file it put in the source tree, named
/// the way content is named everywhere else — "Content/Images/...". The
/// editor copies each one next to the executable, because that is where the
/// game reads content from and an import nobody can see is no import.
/// </summary>
public record ImportResult(bool Ok, string Message, string SetName = "",
    IReadOnlyList<LevelBlock>? Blocks = null,
    IReadOnlyList<LevelDecoration>? Decorations = null,
    IReadOnlyList<string>? Wrote = null);

/// <summary>
/// Reads a PixelLab map export — a .zip or the folder it unpacks to — and
/// turns it into a terrain set this game can paint with, plus the ground of
/// the map it came from.
///
/// IMPORT ONLY. Nothing here writes back to PixelLab, and nothing tries to be
/// a general reader of their format: it reads the parts we need and complains
/// in plain words about the rest.
///
/// WHAT IT TAKES FROM THE EXPORT
///   tile-layers.json   the grid, the sixteen tiles, and where each one sits
///   tile-layers/<id>/  the sixteen pictures
///   objects/           the things standing on the ground, and manifest.json
///
/// WHAT IT IGNORES
///   terrain-tiles/     the same sixteen, re-encoded for their gallery
///   map-composite.png  a picture of the finished map
///   map.json           their bookkeeping, apart from the map's name
///
/// THEIR NAMES ARE NOT KEPT. PixelLab calls its tiles tile_0..tile_15 and its
/// objects after a UUID, and calls every object "base". Everything is renamed
/// on the way in: tiles to mask-00..mask-15, and an object to whatever this
/// game already calls that picture, or to a new name of ours when we have
/// never seen it before.
///
/// THE MASK IS THE INDEX. tile-layers.json carries a "mask" for each tile and
/// it is NOT reliable — one of the two example exports had six of its sixteen
/// mislabelled and four masks missing entirely, while the map itself used all
/// sixteen. tile_6.png is mask 6. Where the file disagrees we say so and carry
/// on, because the index has never been wrong and the label often is.
///
/// Desktop only. It touches zip files and writes into the source tree, neither
/// of which Core is allowed to know about.
/// </summary>
public static class PixelLabImport
{
    /// <summary>How far PixelLab lifts a tile for each step of height.</summary>
    private const int StepPx = 48;

    /// <summary>
    /// Reads an export and writes the set.
    /// </summary>
    /// <param name="askAboutFringe">
    /// Asked once, with the number of bright edge pixels found, when there are
    /// any. Say yes and they are rubbed out; say nothing — no callback, or no
    /// — and the tiles are copied exactly as they came. Nothing is ever
    /// changed without being asked first.
    /// </param>
    public static ImportResult Import(string path, string repoRoot,
        GraphicsDevice? device = null, string? setName = null,
        Func<int, bool>? askAboutFringe = null)
    {
        try
        {
            return path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? FromZip(path, repoRoot, device, setName, askAboutFringe)
                : FromFolder(path, repoRoot, device, setName, askAboutFringe,
                    new DirectoryInfo(path).Name);
        }
        catch (Exception ex)
        {
            return new ImportResult(false, $"could not read {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>
    /// A zip is unpacked to a scratch folder and read as one. Keeping the
    /// export compressed would mean the game reading art out of a zip every
    /// time it drew a square; the sixteen pictures are a few kilobytes each,
    /// so they land as plain files next to all the other art.
    /// </summary>
    private static ImportResult FromZip(string zipPath, string repoRoot,
        GraphicsDevice? device, string? setName, Func<int, bool>? askAboutFringe)
    {
        string scratch = Path.Combine(Path.GetTempPath(),
            "pixellab-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, scratch);
            return FromFolder(scratch, repoRoot, device, setName, askAboutFringe,
                Path.GetFileNameWithoutExtension(zipPath));
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* scratch */ }
        }
    }

    private static ImportResult FromFolder(string folder, string repoRoot,
        GraphicsDevice? device, string? setName, Func<int, bool>? askAboutFringe,
        string fallbackName = "")
    {
        string layersPath = Path.Combine(folder, "tile-layers.json");
        if (!File.Exists(layersPath))
            return new ImportResult(false,
                "that is not a PixelLab export — there is no tile-layers.json in it");

        using var doc = JsonDocument.Parse(File.ReadAllText(layersPath));
        if (!doc.RootElement.TryGetProperty("layers", out var layers) ||
            layers.GetArrayLength() == 0)
            return new ImportResult(false, "tile-layers.json has no layers in it");

        var layer = layers[0];
        var notes = new StringBuilder();

        // ---- geometry: does it fit our grid at all? ----
        var projection = layer.GetProperty("projection");
        var cell = projection.GetProperty("cellSize");
        int cellW = cell.GetProperty("width").GetInt32();
        int cellH = cell.GetProperty("height").GetInt32();
        if (cellW != IsoMath.TileW || cellH != IsoMath.TileH)
            return new ImportResult(false,
                $"those tiles sit on a {cellW}x{cellH} square and this game's squares are " +
                $"{IsoMath.TileW}x{IsoMath.TileH}. Re-export from PixelLab at that size.");

        int tileW = layer.GetProperty("tileW").GetInt32();
        int tileH = layer.GetProperty("tileH").GetInt32();
        int wall = tileH - cellH;

        // ---- the two terrains ----
        var rules = layer.GetProperty("tileRules");
        var terrains = rules.GetProperty("terrains").EnumerateArray()
            .Select(t => t.GetString() ?? "").ToList();
        if (terrains.Count != 2)
            return new ImportResult(false,
                $"this set blends {terrains.Count} terrains; it has to be exactly two");

        if (rules.TryGetProperty("rule_type", out var kind) && kind.GetString() != "corner")
            notes.AppendLine($"  its rule is '{kind.GetString()}', not 'corner' — " +
                "importing it as corner anyway, so check the result");

        // ---- the sixteen pictures ----
        var assets = layer.GetProperty("assetPaths");
        var files = new Dictionary<int, string>();
        foreach (var a in assets.EnumerateObject())
            if (int.TryParse(a.Name, out int i)) files[i] = a.Value.GetString() ?? "";
        var absent = Enumerable.Range(0, 16).Where(m => !files.ContainsKey(m) ||
            !File.Exists(Path.Combine(folder, files[m]))).ToList();
        if (absent.Count > 0)
            return new ImportResult(false,
                "the export is missing tile " + string.Join(", ", absent));

        // The label we do not trust, reported so a bad export is visible.
        if (rules.TryGetProperty("tiles", out var declared))
        {
            var wrong = new List<string>();
            foreach (var t in declared.EnumerateObject())
            {
                int at = t.Name.LastIndexOf('_');
                if (at < 0 || !int.TryParse(t.Name[(at + 1)..], out int index)) continue;
                if (t.Value.TryGetProperty("mask", out var mm) && mm.ValueKind == JsonValueKind.Number
                    && mm.GetInt32() != index)
                    wrong.Add($"{t.Name} says mask {mm.GetInt32()}");
            }
            if (wrong.Count > 0)
                notes.AppendLine($"  {wrong.Count} of its sixteen are mislabelled in the export " +
                    $"({string.Join(", ", wrong.Take(3))}{(wrong.Count > 3 ? ", ..." : "")}). " +
                    "Using the tile's number, which is what the map itself uses.");
        }

        // ---- name it ----
        // Named after its own two terrains, A first: a set called StoneToGrass
        // has A: stone and B: grass, so the name says which way round the
        // masks run. PixelLab's own group name is not used for this — it reads
        // in the opposite order, and a set whose name disagrees with its
        // contents is a trap worth not setting.
        string name = CleanName(setName ?? "");
        if (name.Length == 0) name = CleanName($"{terrains[1]} to {terrains[0]}");
        if (name.Length == 0) name = CleanName(fallbackName);
        if (name.Length == 0) name = "Imported";

        // ---- write the set ----
        string setDir = Path.Combine(repoRoot, "Content", "Images", "Terrain", name);
        Directory.CreateDirectory(setDir);
        var sources = Enumerable.Range(0, 16)
            .Select(m => Path.Combine(folder, files[m])).ToList();
        string Target(int m) => Path.Combine(setDir, $"mask-{m:00}.png");

        // The fringe is counted first and nothing is touched until somebody
        // says so. Pixels are not thrown away on a guess: an export that
        // genuinely has bright edges is still an export we must not alter
        // behind the importer's back.
        var cleaned = device == null ? null : sources.Select(f => Defringed(device, f)).ToList();
        int fringe = cleaned?.Sum(c => c.Cleared) ?? 0;
        bool rub = fringe > 0 && askAboutFringe != null && askAboutFringe(fringe);

        for (int m = 0; m < 16; m++)
            if (rub) WritePng(device!, cleaned![m], Target(m));
            else File.Copy(sources[m], Target(m), overwrite: true);

        if (rub)
            notes.AppendLine($"  rubbed {fringe} pixels of bright fringe off the tile edges, " +
                "which would otherwise have tiled into white bars");
        else if (fringe > 0)
            notes.AppendLine($"  left {fringe} bright edge pixels alone, as asked — " +
                "they may tile into white bars");
        else if (device == null)
            notes.AppendLine("  copied as-is: no graphics device here to look at the edges with");

        // A is mask 0 and B is mask 15, and PixelLab lists them the other way
        // round: a SET bit means terrains[0]. Measured, not assumed — in both
        // example exports mask 0 was the second name in the list and mask 15
        // the first. Get this backwards and every square still draws the right
        // picture, because masks are copied across untouched; it is the editor
        // that then paints grass and gets stone.
        RegisterSet(repoRoot, name, terrains[1], terrains[0], cellW, cellH, wall);

        // ---- the ground of the map it came from ----
        var (blocks, shift) = ReadBlocks(layer, name);

        // ---- and the things standing on it ----
        var decorations = ReadObjects(folder, layer, shift, blocks, repoRoot,
            MapName(folder, fallbackName), device, notes, out var adopted);

        var msg = new StringBuilder($"imported '{name}': {terrains[1]} to {terrains[0]}, " +
            $"{blocks.Count} squares");
        if (decorations.Count > 0) msg.Append($", {decorations.Count} objects");
        if (notes.Length > 0) msg.AppendLine().Append(notes.ToString().TrimEnd());

        var wrote = new List<string> { "Content/Images/Terrain/Terrain.txt" };
        for (int m = 0; m < 16; m++) wrote.Add($"Content/Images/Terrain/{name}/mask-{m:00}.png");
        wrote.AddRange(adopted.Select(f => $"Content/Images/Decorations/{f}"));
        if (adopted.Count > 0) wrote.Add("Content/Images/Decorations/Decorations.txt");

        return new ImportResult(true, msg.ToString(), name, blocks, decorations, wrote);
    }

    /// <summary>
    /// What the map was called in PixelLab, which is what its new art is named
    /// after. Falls back to the file's own name — the zip usually carries the
    /// map's name anyway, but it also picks up whatever a browser or a mail
    /// client stuck on the front of it.
    /// </summary>
    private static string MapName(string folder, string fallback)
    {
        try
        {
            string path = Path.Combine(folder, "map.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("name", out var n) &&
                    n.GetString() is { Length: > 0 } named)
                    return named;
            }
        }
        catch { /* their bookkeeping; a name out of it is a nicety, not a need */ }
        return fallback;
    }

    /// <summary>
    /// PixelLab's grid as pixels: where the CENTRE of a square's top face
    /// lands, and how to go back the other way.
    ///
    /// Calibrated from a tile the export placed itself, not from its
    /// projection block. The two disagree by a pixel — spriteOrigin.x is 47
    /// from origin.x where half a square is 48 — and the placements are what
    /// the map is actually drawn from, so they are the ones to believe.
    /// </summary>
    private readonly record struct Grid(double Ox, double Oy, double Hw, double Hh,
        double StackX, double StackY)
    {
        /// <summary>The square a point on the ground falls in, unrounded.</summary>
        public (double Q, double R) CellAt(double x, double y, int layerY)
        {
            double dx = (x - Ox - StackX * layerY) / Hw;
            double dy = (y - Oy - StackY * layerY) / Hh;
            return ((dy + dx) / 2, (dy - dx) / 2);
        }
    }

    /// <summary>
    /// Works the grid out from the first tile the export placed. A tile
    /// picture is hung by the top-left of its TOP FACE, so that face's centre
    /// is half a square down from where the picture starts — which is the
    /// ground an object standing there would stand on.
    /// </summary>
    private static Grid? GridOf(JsonElement layer)
    {
        if (!layer.TryGetProperty("placements", out var placements) ||
            placements.GetArrayLength() == 0) return null;
        var projection = layer.GetProperty("projection");
        var cell = projection.GetProperty("cellSize");
        double hw = cell.GetProperty("width").GetDouble() / 2;
        double hh = cell.GetProperty("height").GetDouble() / 2;

        double stackX = 0, stackY = -2 * hh;
        if (projection.TryGetProperty("stackBasis", out var stack))
        {
            stackX = stack.GetProperty("x").GetDouble();
            stackY = stack.GetProperty("y").GetDouble();
        }

        var p = placements[0];
        int q = p.GetProperty("q").GetInt32(), r = p.GetProperty("r").GetInt32();
        int ly = p.TryGetProperty("layerY", out var l) ? l.GetInt32() : 0;
        double cx = p.GetProperty("xPx").GetDouble() + p.GetProperty("width").GetDouble() / 2;
        double cy = p.GetProperty("yPx").GetDouble() + hh;
        return new Grid(cx - (q - r) * hw - stackX * ly,
                        cy - (q + r) * hh - stackY * ly, hw, hh, stackX, stackY);
    }

    /// <summary>
    /// The things standing on the map: a trash can, a tree, a pile of tyres.
    ///
    /// EVERY ONE BLOCKS ITS SQUARE, because that is what a decoration is in
    /// this game — Pathfinder will not walk through one. No art is examined to
    /// decide that, and none needs to be: PixelLab has already sorted the map
    /// into ground you walk on and objects standing on it, and that is the
    /// same line. A rug is drawn into the ground layer and arrives as tiles; a
    /// trash can is an object and arrives as this.
    ///
    /// (Nothing hovers yet. The day something is drawn hanging in the air with
    /// floor under it, this is where it would need telling apart.)
    ///
    /// WHICH SQUARE comes from groundBounds — the footprint PixelLab records
    /// for exactly this, the patch of floor the object sits on. Its centre is
    /// turned back into a square, trying each height the map uses and keeping
    /// the one that lands on ground that is really there.
    /// </summary>
    /// <param name="adopted">New decoration art written, for the caller to mirror.</param>
    private static List<LevelDecoration> ReadObjects(string folder, JsonElement layer,
        Point shift, IReadOnlyList<LevelBlock> blocks, string repoRoot, string mapName,
        GraphicsDevice? device, StringBuilder notes, out List<string> adopted)
    {
        adopted = new List<string>();
        var placed = new List<LevelDecoration>();
        string manifestPath = Path.Combine(folder, "objects", "manifest.json");
        if (!File.Exists(manifestPath)) return placed;

        if (GridOf(layer) is not Grid grid)
        {
            notes.AppendLine("  it has objects, but no placed tiles to line them up against — " +
                "the objects were left out");
            return placed;
        }

        var projection = layer.GetProperty("projection").GetProperty("origin");
        double originX = projection.GetProperty("x").GetDouble();
        double originY = projection.GetProperty("y").GetDouble();

        var heights = layer.GetProperty("cells").EnumerateArray()
            .Select(c => c.TryGetProperty("layerY", out var y) ? y.GetInt32() : 0)
            .Distinct().OrderBy(y => y).ToList();
        var ground = blocks.ToDictionary(b => new Point(b.X, b.Y), b => b.Height);
        int feetPerStep = Math.Max(1, StepPx / IsoMath.FootPx);

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!doc.RootElement.TryGetProperty("objects", out var objects)) return placed;

        var names = new DecorationNames(repoRoot, mapName, device, notes);
        int offMap = 0, noFootprint = 0;

        foreach (var o in objects.EnumerateArray())
        {
            if (o.TryGetProperty("visible", out var vis) && vis.ValueKind == JsonValueKind.False)
                continue;
            if (!o.TryGetProperty("groundBounds", out var gb) || gb.ValueKind != JsonValueKind.Object)
            {
                noFootprint++;
                continue;
            }

            // its footprint's centre, moved out of PixelLab's object space and
            // into the one the map itself is drawn in
            double gx = gb.GetProperty("x").GetDouble() + gb.GetProperty("width").GetDouble() / 2 + originX;
            double gy = gb.GetProperty("y").GetDouble() + gb.GetProperty("height").GetDouble() / 2 + originY;

            // One point on the screen is a different square at every height —
            // a thing standing on a square one step up looks exactly like one
            // standing a square further back on the floor. So every height the
            // map uses is tried, and the answer is the one where the square
            // really is that high. That is what tells the two apart; the
            // closest fit only settles the rest.
            Point? best = null;
            double bestErr = double.MaxValue;
            foreach (int ly in heights)
            {
                var (q, r) = grid.CellAt(gx, gy, ly);
                var at = new Point((int)Math.Round(q) - shift.X, (int)Math.Round(r) - shift.Y);
                double err = Math.Abs(q - Math.Round(q)) + Math.Abs(r - Math.Round(r));
                if (!ground.TryGetValue(at, out int height) || height != ly * feetPerStep)
                    err += 10;
                if (err >= bestErr) continue;
                bestErr = err;
                best = at;
            }

            if (best is not Point tile || bestErr >= 10) { offMap++; continue; }

            string? file = names.NameFor(Path.Combine(folder, "objects",
                o.GetProperty("filename").GetString() ?? ""));
            if (file == null) continue;
            placed.Add(new LevelDecoration { X = tile.X, Y = tile.Y, File = file });
        }

        // One square holds one object. The later one would simply overwrite the
        // earlier when the level is read back, so it is dropped here, where it
        // can be said out loud.
        int before = placed.Count;
        placed = placed.GroupBy(d => new Point(d.X, d.Y)).Select(g => g.First()).ToList();
        if (placed.Count < before)
            notes.AppendLine($"  {before - placed.Count} objects were stacked on squares that " +
                "already had one, and were left out — a square holds one object here");

        if (offMap > 0)
            notes.AppendLine($"  {offMap} objects were standing on no square of the map " +
                "and were left out");
        if (noFootprint > 0)
            notes.AppendLine($"  {noFootprint} objects have no groundBounds in the export, " +
                "so there is nothing to say which square they are on — left out");
        notes.Append(names.Report());
        adopted = names.Added;
        return placed;
    }

    /// <summary>
    /// Gives an imported object one of OUR names.
    ///
    /// A picture we already have keeps the name we already call it. Matching
    /// is on PIXELS, not on the file: PixelLab re-encodes on the way out, so
    /// the trash can in the example export is a different file from ours byte
    /// for byte and the same picture pixel for pixel. Same size first, then
    /// every pixel, which is cheap enough at this size and is never wrong.
    ///
    /// Anything we have never seen is written into Decorations as
    /// "{Map} 01.png" and listed in Decorations.txt, so it can be painted
    /// straight away and renamed later if a better name turns up.
    ///
    /// With no graphics device there is nothing to read pixels with, so
    /// everything arrives as new art and the notes say so. That only happens
    /// away from the editor.
    /// </summary>
    private sealed class DecorationNames
    {
        private readonly string _dir, _mapName;
        private readonly GraphicsDevice? _device;
        private readonly StringBuilder _notes;
        private readonly List<string> _added = new();
        private readonly List<string> _reused = new();

        /// <summary>The art this import added, in Decorations.</summary>
        public List<string> Added => _added;

        /// <summary>Pictures already on disk, by name, read once and kept.</summary>
        private readonly Dictionary<string, Color[]> _known = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Point> _size = new(StringComparer.OrdinalIgnoreCase);

        public DecorationNames(string repoRoot, string mapName, GraphicsDevice? device,
            StringBuilder notes)
        {
            _dir = Path.Combine(repoRoot, "Content", "Images", "Decorations");
            _mapName = CleanName(mapName);
            if (_mapName.Length == 0) _mapName = "Imported";
            _device = device;
            _notes = notes;
            Directory.CreateDirectory(_dir);
            if (device == null) return;
            foreach (string f in Directory.GetFiles(_dir, "*.png"))
                Remember(Path.GetFileName(f), f);
        }

        private void Remember(string name, string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var tex = Texture2D.FromStream(_device!, stream);
                var pixels = new Color[tex.Width * tex.Height];
                tex.GetData(pixels);
                _known[name] = pixels;
                _size[name] = new Point(tex.Width, tex.Height);
            }
            catch { /* a picture we cannot read is one we cannot match against */ }
        }

        /// <summary>Our name for this picture, or null if it could not be read.</summary>
        public string? NameFor(string sourcePng)
        {
            if (!File.Exists(sourcePng)) return null;
            if (_device == null) return Adopt(sourcePng, null, 0, 0);

            Color[] pixels;
            int w, h;
            try
            {
                using var stream = File.OpenRead(sourcePng);
                using var tex = Texture2D.FromStream(_device, stream);
                pixels = new Color[tex.Width * tex.Height];
                tex.GetData(pixels);
                w = tex.Width; h = tex.Height;
            }
            catch { return null; }

            foreach (var (name, had) in _known)
                if (_size[name] == new Point(w, h) && had.AsSpan().SequenceEqual(pixels))
                {
                    if (!_reused.Contains(name)) _reused.Add(name);
                    return name;
                }
            return Adopt(sourcePng, pixels, w, h);
        }

        /// <summary>Writes a picture we have never seen under a name of ours.</summary>
        private string Adopt(string sourcePng, Color[]? pixels, int w, int h)
        {
            string name;
            int n = 1;
            do { name = $"{_mapName} {n++:00}.png"; }
            while (File.Exists(Path.Combine(_dir, name)));

            File.Copy(sourcePng, Path.Combine(_dir, name), overwrite: false);
            if (pixels != null) { _known[name] = pixels; _size[name] = new Point(w, h); }
            _added.Add(name);
            Register(name);
            return name;
        }

        /// <summary>Adds the name to Decorations.txt so the editor offers it.</summary>
        private void Register(string name)
        {
            string index = Path.Combine(_dir, "Decorations.txt");
            var lines = File.Exists(index) ? File.ReadAllLines(index).ToList() : new List<string>();
            if (lines.Any(l => l.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))) return;
            lines.Add(name);
            File.WriteAllLines(index, lines);
        }

        public string Report()
        {
            var sb = new StringBuilder();
            if (_device == null && _added.Count > 0)
                sb.AppendLine("  no graphics device here to compare pictures with, so every object " +
                    "came in as new art even if we already had it");
            if (_reused.Count > 0)
                sb.AppendLine($"  {_reused.Count} objects were art we already have, kept under our " +
                    $"names ({string.Join(", ", _reused.Take(3))}" +
                    $"{(_reused.Count > 3 ? ", ..." : "")})");
            if (_added.Count > 0)
                sb.AppendLine($"  {_added.Count} new pictures added to Decorations as " +
                    $"{string.Join(", ", _added.Take(3))}{(_added.Count > 3 ? ", ..." : "")}");
            return sb.ToString();
        }
    }

    /// <summary>One tile with its bright fringe taken off, and how much went.</summary>
    private record Tile(Color[] Pixels, int Width, int Height, int Cleared);

    /// <summary>
    /// Reads a tile and works out what rubbing its fringe off would do — see
    /// EdgeFringe for what the fringe is. Nothing is written here, so this can
    /// be run just to count.
    /// </summary>
    private static Tile Defringed(GraphicsDevice device, string from)
    {
        using var source = File.OpenRead(from);
        using var tex = Texture2D.FromStream(device, source);
        var pixels = new Color[tex.Width * tex.Height];
        tex.GetData(pixels);
        int cleared = EdgeFringe.Clear(pixels, tex.Width, tex.Height);
        return new Tile(pixels, tex.Width, tex.Height, cleared);
    }

    private static void WritePng(GraphicsDevice device, Tile tile, string to)
    {
        using var tex = new Texture2D(device, tile.Width, tile.Height);
        tex.SetData(tile.Pixels);
        using var target = File.Create(to);
        tex.SaveAsPng(target, tile.Width, tile.Height);
    }

    /// <summary>
    /// Every square the map covers. PixelLab's q and r run the same way as our
    /// X and Y — q is down-right on screen and r is down-left — so the grid
    /// needs no turning, only shifting so the lowest square sits at 0,0.
    ///
    /// Height comes from layerY, which lifts a tile by a fixed number of
    /// pixels per step. We measure height in feet, so a step is however many
    /// feet that many pixels comes to.
    /// </summary>
    /// <returns>
    /// The squares, and how far the grid was shifted to put the lowest at 0,0
    /// — which anything else read out of the same export has to be shifted by
    /// too, or it lands on the wrong square.
    /// </returns>
    private static (List<LevelBlock> Blocks, Point Shift) ReadBlocks(JsonElement layer, string setName)
    {
        int feetPerStep = Math.Max(1, StepPx / IsoMath.FootPx);
        var cells = new List<(int q, int r, int y, int tile)>();
        foreach (var c in layer.GetProperty("cells").EnumerateArray())
            cells.Add((
                c.GetProperty("q").GetInt32(),
                c.GetProperty("r").GetInt32(),
                c.TryGetProperty("layerY", out var ly) ? ly.GetInt32() : 0,
                c.GetProperty("tileIndex").GetInt32()));
        if (cells.Count == 0) return (new List<LevelBlock>(), Point.Zero);

        int minQ = cells.Min(c => c.q), minR = cells.Min(c => c.r);

        // One square can appear on more than one layer; the highest wins, which
        // is what you see looking down at the map.
        var top = new Dictionary<Point, (int y, int tile)>();
        foreach (var (q, r, y, tile) in cells)
        {
            var at = new Point(q - minQ, r - minR);
            if (!top.TryGetValue(at, out var had) || y > had.y) top[at] = (y, tile);
        }

        return (top.Select(kv => new LevelBlock
        {
            X = kv.Key.X,
            Y = kv.Key.Y,
            Height = kv.Value.y * feetPerStep,
            Type = $"{setName}{TerrainCatalog.MaskSeparator}{Math.Clamp(kv.Value.tile, 0, 15)}",
            Room = "Main",
        }).OrderBy(b => b.Y).ThenBy(b => b.X).ToList(), new Point(minQ, minR));
    }

    /// <summary>
    /// Adds the set to Terrain.txt, or rewrites its block when it is already
    /// there, so importing the same export twice updates it instead of
    /// declaring it twice.
    /// </summary>
    private static void RegisterSet(string repoRoot, string name, string a, string b,
        int tileW, int tileH, int wall)
    {
        string index = Path.Combine(repoRoot, "Content", "Images", "Terrain", "Terrain.txt");
        var lines = File.Exists(index)
            ? File.ReadAllLines(index).ToList()
            : new List<string>();

        // drop any block already describing this set
        var kept = new List<string>();
        bool skipping = false;
        foreach (var line in lines)
        {
            string t = line.Trim();
            if (t.StartsWith("Set:", StringComparison.OrdinalIgnoreCase))
                skipping = t[4..].Trim().Equals(name, StringComparison.OrdinalIgnoreCase);
            else if (skipping && t.Length == 0) { skipping = false; continue; }
            if (!skipping) kept.Add(line);
        }
        while (kept.Count > 0 && kept[^1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);

        kept.Add("");
        kept.Add($"Set: {name}");
        kept.Add($"A: {a}");
        kept.Add($"B: {b}");
        kept.Add($"Tile: {tileW} x {tileH}");
        kept.Add($"Wall: {wall}");
        kept.Add("");
        File.WriteAllLines(index, kept);
    }

    /// <summary>A folder name out of whatever PixelLab called the group.</summary>
    private static string CleanName(string raw)
    {
        var sb = new StringBuilder();
        bool capitalise = true;
        foreach (char c in raw)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(capitalise ? char.ToUpperInvariant(c) : c);
                capitalise = false;
            }
            else capitalise = true;
        }
        return sb.ToString();
    }
}
