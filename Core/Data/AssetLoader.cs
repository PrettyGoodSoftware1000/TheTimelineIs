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
public enum AssetKind { Map, Background, Sprite, Thumb, Effect }

public class AssetLoader
{
    private readonly GraphicsDevice _device;
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownMissing = new(StringComparer.OrdinalIgnoreCase);
    private Texture2D? _missing;

    public AssetLoader(GraphicsDevice device) => _device = device;

    public static Point OptimalSize(AssetKind kind) => kind switch
    {
        AssetKind.Map => new Point(7680, 4320),
        AssetKind.Background => new Point(3840, 2160),
        AssetKind.Sprite => new Point(1200, 1800),
        AssetKind.Effect => new Point(140, 140),
        _ => new Point(512, 512),
    };

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
    /// How much of a texture's height, as a fraction, is transparent padding
    /// below the last row that has anything drawn on it. 0 means the art runs
    /// right to the bottom edge.
    ///
    /// Cast art is placed by its FEET: the bottom of the drawn rectangle lands
    /// on the square's top face. Art with empty space under the feet therefore
    /// hangs that much too high, and art whose canvas ends above the feet sinks
    /// into the floor. Measuring where the paint actually stops lets both be
    /// placed the same way, whatever the artist left around the edges.
    ///
    /// Measured once per texture and cached — reading pixels back off the GPU
    /// is far too slow to do every frame.
    /// </summary>
    public float BottomPadding(Texture2D tex)
    {
        if (_bottomPad.TryGetValue(tex, out float known)) return known;

        float pad = 0f;
        try
        {
            var pixels = new Color[tex.Width * tex.Height];
            tex.GetData(pixels);
            int lastRow = -1;
            for (int y = tex.Height - 1; y >= 0 && lastRow < 0; y--)
                for (int x = 0; x < tex.Width; x++)
                    if (pixels[y * tex.Width + x].A > OpaqueEnough) { lastRow = y; break; }
            // a fully transparent image has nothing to measure; leave it alone
            if (lastRow >= 0)
                pad = (tex.Height - 1 - lastRow) / (float)tex.Height;
        }
        catch (Exception ex)
        {
            // an unreadable texture is a drawing problem, not a fatal one
            Diagnostics.Current.Warn("AssetLoader", 0,
                $"could not measure a sprite's feet ({ex.Message}); it will sit on its canvas edge");
        }
        _bottomPad[tex] = pad;
        return pad;
    }

    /// <summary>
    /// Alpha at or below this counts as nothing drawn. Art exported with a
    /// soft edge often carries a whisker of alpha well past the visible art,
    /// and treating that as the feet would put the character back where it was.
    /// </summary>
    private const byte OpaqueEnough = 8;

    private readonly Dictionary<Texture2D, float> _bottomPad = new();

    /// <summary>Loads the first path that exists, so thumbnails can fall back to the full sprite.</summary>
    public Texture2D LoadFirstAvailable(params string[] paths)
    {
        for (int i = 0; i < paths.Length - 1; i++)
        {
            var tex = LoadTexture(paths[i], out bool found);
            if (found) return tex;
        }
        return LoadTexture(paths[^1]);
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
