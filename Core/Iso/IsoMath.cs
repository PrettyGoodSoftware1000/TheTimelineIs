using System;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// The isometric projection: 2:1 diamonds, 64x32 ART pixels per tile, with
/// block height in feet lifting things 8 pixels per foot. The camera never
/// rotates. Grid distance is orthogonal 1 / diagonal 2 — which works out to
/// plain Manhattan distance — and every range and movement rule measures with
/// it.
///
/// These are pixels in a source file, not pixels on a window. Nothing here is
/// stretched to fit a screen: PixelCamera multiplies the lot by a whole number,
/// so one art pixel stays square and the same size as every other one.
/// </summary>
public static class IsoMath
{
    public const int TileW = 64;
    public const int TileH = 32;
    public const int FootPx = 8;

    /// <summary>Screen position of the CENTER of a tile's top surface.</summary>
    public static Vector2 ToScreen(int gx, int gy, int heightFeet, Vector2 origin) => new(
        origin.X + (gx - gy) * (TileW / 2f),
        origin.Y + (gx + gy) * (TileH / 2f) - heightFeet * FootPx);

    /// <summary>Grid cell under a screen point, assuming height 0. Callers test raised blocks separately.</summary>
    public static Point ToGrid(Vector2 screen, Vector2 origin)
    {
        float dx = (screen.X - origin.X) / (TileW / 2f);
        float dy = (screen.Y - origin.Y) / (TileH / 2f);
        return new Point(
            (int)Math.Floor((dx + dy) / 2f + 0.5f),
            (int)Math.Floor((dy - dx) / 2f + 0.5f));
    }

    /// <summary>Point-in-diamond test against a tile's top face at its real height.</summary>
    public static bool HitsTop(Vector2 screen, int gx, int gy, int heightFeet, Vector2 origin)
    {
        var c = ToScreen(gx, gy, heightFeet, origin);
        float nx = Math.Abs(screen.X - c.X) / (TileW / 2f);
        float ny = Math.Abs(screen.Y - c.Y) / (TileH / 2f);
        return nx + ny <= 1f;
    }

    /// <summary>Orthogonal step 1, diagonal 2 — i.e. Manhattan distance.</summary>
    public static int GridDistance(Point a, Point b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    /// <summary>
    /// The aim direction snapped to one of the four grid axes — which are the
    /// four DIAGONALS on screen, the only directions a character has a pose
    /// for. A cone is therefore never sprayed straight up, down, left or right
    /// across the screen; an aim that points that way falls to whichever axis
    /// is nearer, and a dead tie goes to the X axis.
    /// </summary>
    private static Point SnapDirection(Point from, Point aim)
    {
        int dx = aim.X - from.X, dy = aim.Y - from.Y;
        if (dx == 0 && dy == 0) return Point.Zero;
        return Math.Abs(dx) >= Math.Abs(dy)
            ? new Point(Math.Sign(dx), 0)
            : new Point(0, Math.Sign(dy));
    }

    /// <summary>
    /// Tiles inside the cone a caster sprays toward an aim point. The shape is
    /// a staircase wedge measured in whole tiles: one tile at depth 1, three at
    /// depth 2, five at depth 3 — the point sits on the square in front of the
    /// caster and the wide end faces away. Formally, a tile is in the cone when
    /// it lies at depth d (1..range) along the heading with an offset of at
    /// most d-1 tiles across it.
    ///
    /// The same shape rotates to all four headings, which on screen are the
    /// four diagonals. There is no straight-up-the-screen cone, because there
    /// is no pose to fire one from.
    /// </summary>
    public static bool InCone(Point from, Point aim, Point tile, int range)
    {
        if (tile == from) return false;
        var dir = SnapDirection(from, aim);
        if (dir == Point.Zero) return false;

        // Rotate the offset into the heading's frame: 'dir' is forward and
        // (dir.Y, -dir.X) is across it.
        int ox = tile.X - from.X, oy = tile.Y - from.Y;
        int depth = ox * dir.X + oy * dir.Y;
        int off = Math.Abs(ox * dir.Y - oy * dir.X);
        return depth >= 1 && depth <= range && off <= depth - 1;
    }
}
