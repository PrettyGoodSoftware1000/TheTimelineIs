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
public class AssetLoader
{
    private readonly GraphicsDevice _device;
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private Texture2D? _missing;

    public AssetLoader(GraphicsDevice device) => _device = device;

    public Texture2D LoadTexture(string path)
    {
        if (_textures.TryGetValue(path, out var cached))
            return cached;
        Texture2D tex;
        try
        {
            using var stream = TitleContainer.OpenStream(path);
            tex = Texture2D.FromStream(_device, stream);
            PremultiplyAlpha(tex);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[assets] missing or unreadable: {path} ({ex.Message})");
            tex = MissingTexture();
        }
        _textures[path] = tex;
        return tex;
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
