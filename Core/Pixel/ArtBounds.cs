using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace TheTimelineIs.Core.Pixel;

/// <summary>
/// Where the picture actually is inside its file.
///
/// Art tools export onto a fixed canvas — 64x64 for one character, 128x128 for
/// another — and pad the rest with nothing. Standing a sprite on a tile by the
/// bottom of its FILE therefore floats it by however much padding the exporter
/// left; the Gun-O-Mancer has 32 empty pixels under his boots. Standing it by
/// the bottom of its opaque pixels puts every character's feet on the ground
/// whatever canvas they came on.
///
/// Read once per texture and remembered, because it reads the whole image back
/// off the card.
/// </summary>
public static class ArtBounds
{
    private static readonly Dictionary<Texture2D, Rectangle> Known = new();

    /// <summary>The box around the non-transparent pixels, in art pixels.</summary>
    public static Rectangle Solid(Texture2D art)
    {
        if (Known.TryGetValue(art, out var found)) return found;

        var pixels = new Color[art.Width * art.Height];
        art.GetData(pixels);

        int left = art.Width, right = -1, top = art.Height, bottom = -1;
        for (int y = 0; y < art.Height; y++)
            for (int x = 0; x < art.Width; x++)
            {
                if (pixels[y * art.Width + x].A == 0) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }

        // an entirely blank picture keeps its own size rather than going to nothing
        var box = right < 0
            ? new Rectangle(0, 0, art.Width, art.Height)
            : new Rectangle(left, top, right - left + 1, bottom - top + 1);
        Known[art] = box;
        return box;
    }
}
