using System;
using System.Collections.Generic;
using System.IO;
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
public enum AssetKind { Map, Background, Sprite, Thumb }

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
            Console.WriteLine($"[assets] missing or unreadable: {path} ({ex.Message})");
            tex = MissingTexture();
            _knownMissing.Add(path);
            found = false;
        }
        _textures[path] = tex;
        return tex;
    }

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

    public static List<string> TryReadLines(string path)
    {
        var lines = new List<string>();
        try
        {
            using var stream = TitleContainer.OpenStream(path);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                    lines.Add(trimmed);
            }
        }
        catch
        {
            // caller treats an empty list as "not found"
        }
        return lines;
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
