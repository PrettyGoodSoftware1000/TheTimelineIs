using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// Loads raw PNGs and text through TitleContainer, the one file door that
/// works identically on desktop and inside a mobile app bundle. Never uses
/// System.IO.File for game assets. Textures are cached and premultiplied
/// so they composite correctly with the sprite batch.
/// </summary>
/// <summary>
/// The size each kind of art is authored at. Undersized uploads are scaled up
/// to match (see <see cref="DisplaySize"/>); oversized ones are left alone.
/// </summary>
/// <summary>The one kind of art that is still sized to fit: the painted world map.</summary>
public enum AssetKind { Map }

public class AssetLoader
{
    private readonly GraphicsDevice _device;
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownMissing = new(StringComparer.OrdinalIgnoreCase);
    private Texture2D? _missing;

    public AssetLoader(GraphicsDevice device) => _device = device;

    public static Point OptimalSize(AssetKind kind) => new(7680, 4320);

    /// <summary>
    /// How big this texture should render. If it's smaller than the optimal
    /// size for its kind, scale up until its LONGER side matches the
    /// corresponding optimal dimension; aspect ratio is always preserved, and
    /// art that's already big enough renders at its native size.
    /// </summary>
    public static Vector2 DisplaySize(Texture2D tex, AssetKind kind)
    {
        var optimal = OptimalSize(kind);
        float scale = tex.Width >= tex.Height
            ? optimal.X / (float)tex.Width
            : optimal.Y / (float)tex.Height;
        return scale > 1f
            ? new Vector2(tex.Width * scale, tex.Height * scale)
            : new Vector2(tex.Width, tex.Height);
    }

    public Texture2D LoadTexture(string path) => LoadTexture(path, out _);

    /// <param name="found">False when the file was missing and a placeholder was substituted.</param>
    public Texture2D LoadTexture(string path, out bool found)
    {
        if (_textures.TryGetValue(path, out var cached))
        {
            found = !_knownMissing.Contains(path);
            return cached;
        }
        Texture2D tex;
        try
        {
            using var stream = TitleContainer.OpenStream(path);
            tex = Texture2D.FromStream(_device, stream);
            PremultiplyAlpha(tex);
            found = true;
        }
        catch (Exception ex)
        {
            Diagnostics.Current.Error(path, 0, $"image missing or unreadable ({ex.Message})");
            tex = MissingTexture();
            _knownMissing.Add(path);
            found = false;
        }
        _textures[path] = tex;
        return tex;
    }

    /// <summary>
    /// Alpha at or below this counts as nothing drawn. Art exported with a
    /// soft edge often carries a whisker of alpha well past the visible art,
    /// and treating that as the feet would put the character back where it was.
    /// </summary>
    private const byte OpaqueEnough = 8;


    /// <summary>
    /// Alpha at or below this is nothing. Deliberately lower than the
    /// eight <see cref="OpaqueEnough"/> uses, because an outline is meant to
    /// follow the SEEN edge of the art, and a soft edge is seen.
    /// </summary>
    private const byte AnyPaint = 0;

    private readonly Dictionary<(Texture2D, int), Texture2D> _outlines = new();

    /// <summary>
    /// A white silhouette outline of a texture: transparent everywhere except a
    /// band of the given thickness hugging the outside of the drawn pixels.
    /// Tint it and stretch it over the sprite and you get a border that follows
    /// the ART rather than the edges of its canvas — which for a character
    /// standing in a mostly-empty PNG is a completely different shape.
    ///
    /// Built once per texture and cached: it walks every pixel twice, which is
    /// far too slow to do per frame but nothing as a one-off.
    /// </summary>
    public Texture2D Outline(Texture2D tex, int thickness)
    {
        var key = (tex, thickness);
        if (_outlines.TryGetValue(key, out var known)) return known;

        int w = tex.Width, h = tex.Height;
        var pixels = new Color[w * h];
        var outline = new Texture2D(_device, w, h);
        try
        {
            tex.GetData(pixels);
            var paint = new bool[w * h];
            for (int i = 0; i < pixels.Length; i++) paint[i] = pixels[i].A > AnyPaint;

            // Dilating a square is the same as dilating sideways and then
            // downwards, which turns a (2t+1)^2 look per pixel into 2(2t+1).
            var wide = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    for (int d = -thickness; d <= thickness; d++)
                    {
                        int nx = x + d;
                        if (nx < 0 || nx >= w || !paint[y * w + nx]) continue;
                        wide[y * w + x] = true;
                        break;
                    }
                }
            var grown = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    for (int d = -thickness; d <= thickness; d++)
                    {
                        int ny = y + d;
                        if (ny < 0 || ny >= h || !wide[ny * w + x]) continue;
                        grown[y * w + x] = true;
                        break;
                    }
                }

            // the band is what the growing added: inside it, but not art
            var edge = new Color[w * h];
            for (int i = 0; i < edge.Length; i++)
                edge[i] = grown[i] && !paint[i] ? Color.White : Color.Transparent;
            outline.SetData(edge);
        }
        catch (Exception ex)
        {
            // an unreadable texture means no outline, not a crash
            Diagnostics.Current.Warn("AssetLoader", 0,
                $"could not trace a sprite's outline ({ex.Message})");
        }
        _outlines[key] = outline;
        return outline;
    }

    public static List<string> TryReadLines(string path) =>
        ReadNumbered(path, null).Select(l => l.Text).ToList();

    /// <summary>
    /// Content lines with their 1-based line numbers, so a complaint can point
    /// at the exact line. Blank and '#' lines are skipped but still counted.
    /// Passing a source name reports a missing file as an error.
    /// </summary>
    public static List<(int Line, string Text)> ReadNumbered(string path, string? reportAs)
    {
        var lines = new List<(int, string)>();
        try
        {
            using var stream = TitleContainer.OpenStream(path);
            using var reader = new StreamReader(stream);
            string? line;
            int n = 0;
            while ((line = reader.ReadLine()) != null)
            {
                n++;
                string trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                    lines.Add((n, trimmed));
            }
        }
        catch (Exception ex)
        {
            if (reportAs != null)
                Diagnostics.Current.Error(reportAs, 0, $"could not be read: {ex.Message}");
        }
        return lines;
    }

    private static readonly Dictionary<string, bool> ExistsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a content file is actually present, for the validator.</summary>
    public static bool Exists(string path)
    {
        if (ExistsCache.TryGetValue(path, out bool known)) return known;
        bool found;
        try
        {
            using var stream = TitleContainer.OpenStream(path);
            found = true;
        }
        catch
        {
            found = false;
        }
        ExistsCache[path] = found;
        return found;
    }

    private static void PremultiplyAlpha(Texture2D tex)
    {
        var pixels = new Color[tex.Width * tex.Height];
        tex.GetData(pixels);
        for (int i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            if (p.A == 255) continue;
            pixels[i] = new Color(
                (byte)(p.R * p.A / 255), (byte)(p.G * p.A / 255),
                (byte)(p.B * p.A / 255), p.A);
        }
        tex.SetData(pixels);
    }

    private Texture2D MissingTexture()
    {
        if (_missing != null) return _missing;
        _missing = new Texture2D(_device, 64, 64);
        var pixels = new Color[64 * 64];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = ((i % 64) / 8 + (i / 64) / 8) % 2 == 0 ? Color.Magenta : Color.Black;
        _missing.SetData(pixels);
        return _missing;
    }
}
