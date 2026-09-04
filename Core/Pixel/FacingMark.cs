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

        var px = new Color[Width * Height];
        var ink = new Color(255, 214, 40);

        // A quarter of the ground diamond, pointing at one of its four corners.
        // Which quarter is filled is the whole of the difference between the
        // four directions.
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                // where this pixel sits inside the diamond, -1..1 on each axis
                float nx = (x - Width / 2f) / (Width / 2f);
                float ny = (y - Height / 2f) / (Height / 2f);
                if (System.Math.Abs(nx) + System.Math.Abs(ny) > 1f) continue;   // outside it

                bool inWedge = key.Item2 switch
                {
                    // +X on the grid is down-right on screen, +Y is down-left
                    Facing8.SouthEast => nx >= 0 && ny >= 0,
                    Facing8.SouthWest => nx <= 0 && ny >= 0,
                    Facing8.NorthEast => nx >= 0 && ny <= 0,
                    _ => nx <= 0 && ny <= 0,
                };
                if (inWedge) px[y * Width + x] = ink;
            }

        var tex = new Texture2D(device, Width, Height);
        tex.SetData(px);
        return Made[key] = tex;
    }
}
