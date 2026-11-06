using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// Rubs the bright fringe off the left and right edges of a tile.
///
/// PixelLab strokes a near-white line one or two pixels wide down the extreme
/// left and right of every tile it exports. On its own that looks like an
/// outline; laid out on a grid, where those edges are exactly where one tile
/// meets the next, it reads as white bars running down the map.
///
/// The rule walks in from each end of a row and clears pixels while they are
/// much brighter than this art ever gets, stopping at the first real one. It
/// cannot eat into the middle of a tile, because it stops at the first pixel
/// that is not part of the fringe.
///
/// The threshold was measured, not picked: across the sixteen tiles of the
/// first imported set, the fringe sits at 253 and 183 on every channel while
/// ninety-nine per cent of the art is below 118. Anything over 150 on all
/// three channels is the fringe.
/// </summary>
public static class EdgeFringe
{
    /// <summary>Brighter than this on every channel, at the edge, is the fringe.</summary>
    public const int TooBright = 150;

    /// <summary>
    /// Clears the fringe in place and says how many pixels went. Takes the
    /// pixels straight rather than a texture so it can be checked without a
    /// graphics card.
    /// </summary>
    public static int Clear(Color[] pixels, int width, int height)
    {
        int cleared = 0;
        for (int y = 0; y < height; y++)
        {
            cleared += Sweep(pixels, width, y, from: 0, step: 1);
            cleared += Sweep(pixels, width, y, from: width - 1, step: -1);
        }
        return cleared;
    }

    /// <summary>
    /// One row, from one end. Transparent pixels are stepped over — the
    /// fringe starts wherever the picture does — and the first solid pixel
    /// that is not fringe ends it.
    /// </summary>
    private static int Sweep(Color[] pixels, int width, int row, int from, int step)
    {
        int cleared = 0;
        for (int x = from; x >= 0 && x < width; x += step)
        {
            int i = row * width + x;
            var p = pixels[i];
            if (p.A == 0) continue;
            if (p.R > TooBright && p.G > TooBright && p.B > TooBright)
            {
                pixels[i] = Color.Transparent;
                cleared++;
            }
            else break;
        }
        return cleared;
    }
}
