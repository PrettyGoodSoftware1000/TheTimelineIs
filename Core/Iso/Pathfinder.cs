using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using TheTimelineIs.Core.Data;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// Grid movement rules in one place. Step cost: orthogonal 1, diagonal 2,
/// plus 1 per foot of height climbed; more than 4 feet up in one step is
/// impassable; any drop is free. Closed doors, decorations, characters, and
/// empty space all block.
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
    /// Dijkstra out to the budget. Returns cost to every reachable tile and
    /// the parent map for walking the path back.
    /// </summary>
    public static (Dictionary<Point, int> Cost, Dictionary<Point, Point> Parent) Reachable(
        LevelData level, Point from, int budget,
        IReadOnlySet<string> revealedRooms, IReadOnlySet<Point> occupied)
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
                if (!Standable(level, next, revealedRooms) || occupied.Contains(next)) continue;

                int rise = (level.BlockAt(next)?.Height ?? 0) - hereHeight;
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
        out List<Point> path)
    {
        path = new List<Point>();
        var (cost, parent) = Reachable(level, from, budget, revealedRooms, occupied);
        // already close enough?
        if (IsoMath.GridDistance(from, target) <= stopAtRange) return null;

        Point? best = null;
        int bestDist = int.MaxValue, bestCost = int.MaxValue;
        foreach (var (tile, c) in cost)
        {
            int d = IsoMath.GridDistance(tile, target);
            if (d < bestDist || (d == bestDist && c < bestCost))
            {
                best = tile;
                bestDist = d;
                bestCost = c;
            }
        }
        if (best is Point goal && bestDist < IsoMath.GridDistance(from, target))
        {
            path = PathTo(parent, from, goal);
            return goal;
        }
        return null;
    }
}
