using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using TheTimelineIs.Core.Iso;

namespace TheTimelineIs.Desktop;

/// <summary>What an import produced, or why it didn't.</summary>
public record ImportResult(bool Ok, string Message, string SetName = "",
    IReadOnlyList<LevelBlock>? Blocks = null);

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
///
/// WHAT IT IGNORES
///   terrain-tiles/     the same sixteen, re-encoded for their gallery
///   map-composite.png  a picture of the finished map
///   map.json           their editor's own bookkeeping
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

    public static ImportResult Import(string path, string repoRoot, string? setName = null)
    {
        try
        {
            return path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? FromZip(path, repoRoot, setName)
                : FromFolder(path, repoRoot, setName, new DirectoryInfo(path).Name);
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
    private static ImportResult FromZip(string zipPath, string repoRoot, string? setName)
    {
        string scratch = Path.Combine(Path.GetTempPath(),
            "pixellab-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, scratch);
            return FromFolder(scratch, repoRoot, setName,
                Path.GetFileNameWithoutExtension(zipPath));
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* scratch */ }
        }
    }

    private static ImportResult FromFolder(string folder, string repoRoot, string? setName,
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
        for (int m = 0; m < 16; m++)
            File.Copy(Path.Combine(folder, files[m]),
                Path.Combine(setDir, $"mask-{m:00}.png"), overwrite: true);

        // A is mask 0 and B is mask 15, and PixelLab lists them the other way
        // round: a SET bit means terrains[0]. Measured, not assumed — in both
        // example exports mask 0 was the second name in the list and mask 15
        // the first. Get this backwards and every square still draws the right
        // picture, because masks are copied across untouched; it is the editor
        // that then paints grass and gets stone.
        RegisterSet(repoRoot, name, terrains[1], terrains[0], cellW, cellH, wall);

        // ---- the ground of the map it came from ----
        var blocks = ReadBlocks(layer, name);

        var msg = new StringBuilder($"imported '{name}': {terrains[1]} to {terrains[0]}, " +
            $"{blocks.Count} squares");
        if (notes.Length > 0) msg.AppendLine().Append(notes.ToString().TrimEnd());
        return new ImportResult(true, msg.ToString(), name, blocks);
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
    private static List<LevelBlock> ReadBlocks(JsonElement layer, string setName)
    {
        int feetPerStep = Math.Max(1, StepPx / IsoMath.FootPx);
        var cells = new List<(int q, int r, int y, int tile)>();
        foreach (var c in layer.GetProperty("cells").EnumerateArray())
            cells.Add((
                c.GetProperty("q").GetInt32(),
                c.GetProperty("r").GetInt32(),
                c.TryGetProperty("layerY", out var ly) ? ly.GetInt32() : 0,
                c.GetProperty("tileIndex").GetInt32()));
        if (cells.Count == 0) return new List<LevelBlock>();

        int minQ = cells.Min(c => c.q), minR = cells.Min(c => c.r);

        // One square can appear on more than one layer; the highest wins, which
        // is what you see looking down at the map.
        var top = new Dictionary<Point, (int y, int tile)>();
        foreach (var (q, r, y, tile) in cells)
        {
            var at = new Point(q - minQ, r - minR);
            if (!top.TryGetValue(at, out var had) || y > had.y) top[at] = (y, tile);
        }

        return top.Select(kv => new LevelBlock
        {
            X = kv.Key.X,
            Y = kv.Key.Y,
            Height = kv.Value.y * feetPerStep,
            Type = $"{setName}{TerrainCatalog.MaskSeparator}{Math.Clamp(kv.Value.tile, 0, 15)}",
            Room = "Main",
        }).OrderBy(b => b.Y).ThenBy(b => b.X).ToList();
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
