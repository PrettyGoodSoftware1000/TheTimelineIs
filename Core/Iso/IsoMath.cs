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

    /// <summary>How fast the wedge fans out per tile of depth.</summary>
    private const float ConeSpread = 0.62f;

    /// <summary>
    /// Tiles inside a wedge running from the caster toward an aim point. The
    /// half-width is measured across the centre line and grows with depth, so
    /// the point of the cone sits on the caster and the wide end faces away.
    /// </summary>
    public static bool InCone(Point from, Point aim, Point tile, int range)
    {
        if (tile == from) return false;
        if (GridDistance(from, tile) > range) return false;

        var dir = new Vector2(aim.X - from.X, aim.Y - from.Y);
        var to = new Vector2(tile.X - from.X, tile.Y - from.Y);
        if (dir.LengthSquared() < 0.001f) return false;
        dir.Normalize();

        float depth = Vector2.Dot(to, dir);                    // how far along the centre line
        if (depth < 0.5f) return false;                        // nothing behind the caster
        float across = (to - dir * depth).Length();            // how far off that line
        return across <= (depth - 0.5f) * ConeSpread + 0.5f;
    }
}
