using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using TheTimelineIs.Core.Data;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// Grid movement rules in one place. Step cost: orthogonal 1, diagonal 2,
/// plus 1 per foot of height climbed; more than 4 feet up in one step is
/// impassable; any drop is free. Closed doors, decorations, enemies, and
/// empty space all block. Friendly characters can be walked through but not
/// stopped on.
/// </summary>
public static class Pathfinder
{
    public const int MaxStepUpFeet = 4;

    private static readonly (int dx, int dy, int cost)[] Steps =
    {
        (1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1),
        (1, 1, 2), (1, -1, 2), (-1, 1, 2), (-1, -1, 2),
    };

    /// <summary>Whether a tile can be stood on at all (terrain only, ignoring who's there).</summary>
    public static bool Standable(LevelData level, Point p, IReadOnlySet<string> revealedRooms)
    {
        var block = level.BlockAt(p);
        if (block == null || !revealedRooms.Contains(block.Room)) return false;
        if (level.DecorationAt(p) != null) return false;
        if (level.DoorAt(p) is LevelDoor door && !door.Open) return false;
        return true;
    }

    /// <summary>
    /// Every square a size x size body anchored at <paramref name="anchor"/>
    /// covers. Size 1 is the ordinary case and yields the anchor alone.
    /// </summary>
    public static IEnumerable<Point> Footprint(Point anchor, int sizeX, int sizeY)
    {
        for (int y = 0; y < sizeY; y++)
            for (int x = 0; x < sizeX; x++)
                yield return new Point(anchor.X + x, anchor.Y + y);
    }

    /// <summary>A square footprint, for callers that only have one number.</summary>
    public static IEnumerable<Point> Footprint(Point anchor, int size) =>
        Footprint(anchor, size, size);

    /// <summary>
    /// Whether a body of the given size fits with its corner on this square.
    /// Every tile under it has to be standable, clear of other people, and at
    /// the same height — a four-tile body cannot straddle a step.
    /// </summary>
    public static bool Fits(LevelData level, Point anchor, int sizeX, int sizeY,
        IReadOnlySet<string> revealedRooms, IReadOnlySet<Point> occupied,
        IReadOnlySet<Point>? passThrough = null)
    {
        int? height = null;
        foreach (var tile in Footprint(anchor, sizeX, sizeY))
        {
            if (!Standable(level, tile, revealedRooms)) return false;
            if (occupied.Contains(tile) && !(passThrough?.Contains(tile) ?? false)) return false;
            int h = level.BlockAt(tile)?.Height ?? 0;
            if (height is int known && known != h) return false;
            height = h;
        }
        return true;
    }

    /// <summary>
    /// Dijkstra out to the budget. Returns cost to every reachable tile and
    /// the parent map for walking the path back.
    /// </summary>
    public static (Dictionary<Point, int> Cost, Dictionary<Point, Point> Parent) Reachable(
        LevelData level, Point from, int budget,
        IReadOnlySet<string> revealedRooms, IReadOnlySet<Point> occupied,
        bool ignoreHeight = false, IReadOnlySet<Point>? passThrough = null,
        int sizeX = 1, int sizeY = 1)
    {
        var cost = new Dictionary<Point, int> { [from] = 0 };
        var parent = new Dictionary<Point, Point>();
        var queue = new PriorityQueue<Point, int>();
        queue.Enqueue(from, 0);

        while (queue.TryDequeue(out var here, out int c))
        {
            if (c > cost[here]) continue;
            int hereHeight = level.BlockAt(here)?.Height ?? 0;

            foreach (var (dx, dy, stepCost) in Steps)
            {
                var next = new Point(here.X + dx, here.Y + dy);
                // friends can be squeezed past, but not stood on — see the trim below
                bool crossable = passThrough != null && passThrough.Contains(next);
                if (sizeX > 1 || sizeY > 1)
                {
                    if (!Fits(level, next, sizeX, sizeY, revealedRooms, occupied, passThrough)) continue;
                }
                else
                {
                    if (!Standable(level, next, revealedRooms)) continue;
                    if (occupied.Contains(next) && !crossable) continue;
                }

                // a Leap goes over terrain: no climb cost, no height limit
                int rise = ignoreHeight ? 0 : (level.BlockAt(next)?.Height ?? 0) - hereHeight;
                if (rise > MaxStepUpFeet) continue;
                int total = c + stepCost + Math.Max(0, rise);   // climbing costs 1 per foot; drops are free
                if (total > budget) continue;
                if (cost.TryGetValue(next, out int known) && known <= total) continue;

                cost[next] = total;
                parent[next] = here;
                queue.Enqueue(next, total);
            }
        }
        cost.Remove(from);
        if (passThrough != null)
            foreach (var tile in passThrough)
                cost.Remove(tile);   // walked over, never landed on
        return (cost, parent);
    }

    /// <summary>Walk the parent map back into a start-to-goal tile list (start excluded).</summary>
    public static List<Point> PathTo(Dictionary<Point, Point> parent, Point from, Point goal)
    {
        var path = new List<Point>();
        var here = goal;
        while (here != from && parent.TryGetValue(here, out var prev))
        {
            path.Add(here);
            here = prev;
        }
        path.Reverse();
        return path;
    }

    /// <summary>Best reachable tile adjacent-enough to a target: enemies chase with this.</summary>
    public static Point? StepToward(LevelData level, Point from, Point target, int budget,
        int stopAtRange, IReadOnlySet<string> revealedRooms, IReadOnlySet<Point> occupied,
        out List<Point> path, int sizeX = 1, int sizeY = 1)
    {
        path = new List<Point>();
        var (cost, parent) = Reachable(level, from, budget, revealedRooms, occupied,
            sizeX: sizeX, sizeY: sizeY);

        // distance from the whole body, not just its corner, so a big enemy
        // stops as soon as any part of it is close enough
        int Gap(Point anchor) => Footprint(anchor, sizeX, sizeY).Min(t => IsoMath.GridDistance(t, target));

        int here = Gap(from);
        if (here <= stopAtRange) return null;   // already close enough?

        Point? best = null;
        int bestDist = int.MaxValue, bestCost = int.MaxValue;
        foreach (var (tile, c) in cost)
        {
            int d = Gap(tile);
            if (d < bestDist || (d == bestDist && c < bestCost))
            {
                best = tile;
                bestDist = d;
                bestCost = c;
            }
        }
        if (best is Point goal && bestDist < here)
        {
            path = PathTo(parent, from, goal);
            return goal;
        }
        return null;
    }
}
