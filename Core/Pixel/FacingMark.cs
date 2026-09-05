using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace TheTimelineIs.Core.Pixel;

/// <summary>
/// The yellow triangle on the ground that says which way a placeholder is
/// turned.
///
/// A cube has no front, so without this a placeholder gives no clue what it is
/// facing — and facing is exactly what the four rotations are about. The
/// triangle points along the square's own diagonals, so it lies flat on the
/// floor like a shadow rather than standing up like a sign.
///
/// One picture per direction, drawn once and kept.
/// </summary>
public static class FacingMark
{
    /// <summary>The mark's footprint, half a tile across.</summary>
    public const int Width = 32;
    public const int Height = 16;

    private static readonly Dictionary<(GraphicsDevice, Facing8), Texture2D> Made = new();

    public static Texture2D For(GraphicsDevice device, Facing8 facing)
    {
        var key = (device, facing.Nearest());
        if (Made.TryGetValue(key, out var known)) return known;
        var tex = new Texture2D(device, Width, Height);
        tex.SetData(Pixels(key.Item2));
        return Made[key] = tex;
    }

    /// <summary>
    /// The mark's pixels, apart from any graphics card. Separated so which way
    /// it points can be checked without a window — it pointed backwards once,
    /// and reading a triangle off a screenshot is no way to settle that.
    /// </summary>
    public static Color[] Pixels(Facing8 facing)
    {
        var px = new Color[Width * Height];
        var ink = new Color(255, 214, 40);

        // Which way the neighbouring square lies, in this box's own terms: the
        // mark is exactly half a tile, so the square ahead sits on one of its
        // four corners. +X on the grid is down-right on screen, +Y down-left.
        var (fx, fy) = facing.Nearest() switch
        {
            Facing8.SouthEast => (1f, 1f),
            Facing8.SouthWest => (-1f, 1f),
            Facing8.NorthEast => (1f, -1f),
            _ => (-1f, -1f),
        };

        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                // -1..1 across the box on each axis
                float nx = (x - (Width - 1) / 2f) / (Width / 2f);
                float ny = (y - (Height - 1) / 2f) / (Height / 2f);

                // turned into "how far ahead" and "how far across", so the same
                // arrow serves all four directions without four shapes
                float ahead = (nx * fx + ny * fy) / 2f;
                float across = (nx * fy - ny * fx) / 2f;

                // an arrowhead: widest at the character's feet, coming to a
                // point on the square they are facing. A quarter of the diamond
                // was the shape before, and nobody could tell which end of it
                // was the front.
                if (ahead >= 0f && ahead <= 1f && Math.Abs(across) <= (1f - ahead) * 0.62f)
                    px[y * Width + x] = ink;
            }
        return px;
    }
}
