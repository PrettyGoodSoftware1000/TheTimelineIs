using System;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// The isometric projection: 2:1 diamonds, 360x180 px per tile, with block
/// height in feet lifting things 90 px per foot. The camera never rotates.
/// Grid distance is orthogonal 1 / diagonal 2 — which works out to plain
/// Manhattan distance — and every range and movement rule measures with it.
/// </summary>
public static class IsoMath
{
    public const int TileW = 360;
    public const int TileH = 180;
    public const int FootPx = 90;

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
    /// The aim direction snapped to one of the eight compass headings. Each
    /// heading owns a 45-degree slice, so tan(22.5) = 0.4142 is the cutoff
    /// between "straight along an axis" and "diagonal".
    /// </summary>
    private static Point SnapDirection(Point from, Point aim)
    {
        int dx = aim.X - from.X, dy = aim.Y - from.Y;
        int ax = Math.Abs(dx), ay = Math.Abs(dy);
        int sx = Math.Sign(dx), sy = Math.Sign(dy);
        if (ay <= 0.4142f * ax) return new Point(sx, 0);
        if (ax <= 0.4142f * ay) return new Point(0, sy);
        return new Point(sx, sy);
    }

    /// <summary>
    /// Tiles inside the cone a caster sprays toward an aim point. The shape is
    /// a staircase wedge measured in whole tiles: one tile at depth 1, three at
    /// depth 2, five at depth 3 — the point sits on the square in front of the
    /// caster and the wide end faces away. Formally, a tile is in the cone when
    /// it lies at depth d (1..range) along the heading with an offset of at
    /// most d-1 tiles across it.
    ///
    /// The same shape rotates to all eight headings. For a diagonal heading the
    /// wedge is measured in diagonal steps, which keeps it exactly congruent to
    /// the orthogonal one rather than fanning out over twice the ground.
    /// </summary>
    public static bool InCone(Point from, Point aim, Point tile, int range)
    {
        if (tile == from) return false;
        var dir = SnapDirection(from, aim);
        if (dir == Point.Zero) return false;

        // Rotate the offset into the heading's frame: 'dir' is forward and
        // (dir.Y, -dir.X) is across it. A diagonal heading spans two grid steps
        // per unit of depth, hence the divide.
        int ox = tile.X - from.X, oy = tile.Y - from.Y;
        int span = dir.X * dir.X + dir.Y * dir.Y;          // 1 orthogonal, 2 diagonal
        int forward = ox * dir.X + oy * dir.Y;
        int across = ox * dir.Y - oy * dir.X;
        if (forward % span != 0 || across % span != 0) return false;  // off the lattice

        int depth = forward / span;
        int off = Math.Abs(across / span);
        return depth >= 1 && depth <= range && off <= depth - 1;
    }
}
