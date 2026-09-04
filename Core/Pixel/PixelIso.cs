using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Pixel;

/// <summary>
/// The isometric grid for the pixel build, measured in ART pixels rather than
/// in the 3840x2160 design space the rest of the game uses.
///
/// Every number here is a whole number of pixels in a source file. Nothing is
/// scaled to fit a window: zooming multiplies the lot by a whole number, so a
/// pixel stays square and the same size as every other pixel.
/// </summary>
public static class PixelIso
{
    /// <summary>A tile's top face: the classic 2:1 diamond, 64 across.</summary>
    public const int TileW = 64;
    public const int TileH = 32;

    /// <summary>How far one foot of block height lifts a tile up the screen.</summary>
    public const int FootPx = 8;

    /// <summary>Top-left corner of a tile's 64x32 cell, in world pixels.</summary>
    public static Point CellAt(int gx, int gy, int heightFeet) => new(
        (gx - gy) * (TileW / 2) - TileW / 2,
        (gx + gy) * (TileH / 2) - heightFeet * FootPx);

    /// <summary>
    /// The point a character standing on this square puts their feet: the
    /// middle of the diamond, not its top corner.
    /// </summary>
    public static Point FootOf(int gx, int gy, int heightFeet)
    {
        var cell = CellAt(gx, gy, heightFeet);
        return new Point(cell.X + TileW / 2, cell.Y + TileH / 2);
    }

    /// <summary>
    /// The square under a world point, ignoring height. The inverse of the
    /// projection above, which is exact because both halves are whole pixels.
    /// </summary>
    public static Point GridAt(Vector2 world)
    {
        // measured from the middle of square 0,0, which is half a cell below
        // that cell's top corner
        float fx = world.X / (TileW / 2f);
        float fy = (world.Y - TileH / 2f) / (TileH / 2f);
        return new Point(
            (int)System.Math.Floor((fx + fy) / 2f + 0.5f),
            (int)System.Math.Floor((fy - fx) / 2f + 0.5f));
    }
}
