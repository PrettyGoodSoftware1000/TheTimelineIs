using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Iso;

/// <summary>What happened on one square of a mower's run.</summary>
public enum MowerStep
{
    /// <summary>Rolled over open ground, hitting nothing.</summary>
    Rolled,

    /// <summary>Wandered a square sideways before carrying on the same heading.</summary>
    Drifted,

    /// <summary>Hit somebody and killed them, and went straight through.</summary>
    Through,

    /// <summary>Hit somebody who stayed up, and glanced off in a new direction.</summary>
    Bounced,

    /// <summary>Went up. Nothing happens after this.</summary>
    Exploded,
}

/// <summary>One square of the run: where the mower was, and what it did there.</summary>
public record MowerBeat(Point Tile, MowerStep What, string? Hit = null, int Damage = 0);

/// <summary>
/// The whole flight of a lawnmower, worked out before any of it is drawn.
///
/// Doing it in one go rather than a square at a time is what makes it
/// testable: the run is a list of beats that the screen then plays back, and
/// the rules below can be checked without a graphics device or a level screen.
/// Everything random comes from the Random handed in, so a test can pin it.
/// </summary>
public class MowerRun
{
    /// <summary>Squares travelled before the machine starts wandering.</summary>
    public const int StraightTiles = 4;

    /// <summary>Chance per square, once past StraightTiles, of drifting one square sideways.</summary>
    public const float DriftChance = 0.05f;

    /// <summary>Chance that any given contact sets the whole thing off.</summary>
    public const float ExplodeChance = 0.40f;

    public List<MowerBeat> Beats { get; } = new();

    /// <summary>Where it went up, or null if it somehow never did.</summary>
    public Point? Blast { get; private set; }

    /// <summary>The four ways a mower can be pointed. Never diagonally: it drives, it does not fly.</summary>
    public static readonly Point[] Headings =
        { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

    /// <summary>Every direction it can glance off in, diagonals included.</summary>
    public static readonly Point[] Bounces =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        new(1, 1), new(1, -1), new(-1, 1), new(-1, -1),
    };

    /// <summary>
    /// The heading nearest the way the player pointed, snapped to an axis. A
    /// diagonal aim goes whichever way it leans further; a dead-even diagonal
    /// goes across rather than down, which is arbitrary but has to be decided.
    /// </summary>
    public static Point HeadingToward(Point from, Point aim)
    {
        int dx = aim.X - from.X, dy = aim.Y - from.Y;
        if (dx == 0 && dy == 0) return Headings[0];
        return Math.Abs(dx) >= Math.Abs(dy)
            ? new Point(Math.Sign(dx), 0)
            : new Point(0, Math.Sign(dy));
    }

    /// <summary>
    /// Drives the machine and records what it does.
    /// </summary>
    /// <param name="start">The caster's square. The mower starts on the next one.</param>
    /// <param name="heading">One of <see cref="Headings"/>.</param>
    /// <param name="maxTiles">Squares it can cross before it goes up on its own.</param>
    /// <param name="ground">Whether a square is real, revealed floor.</param>
    /// <param name="occupant">Who is standing on a square, or null.</param>
    /// <param name="strike">
    /// Deals the contact damage and answers how much it did and whether that
    /// killed them. Kept as a callback so the run does no damage of its own —
    /// the screen owns that, and the simulation stays something a test can drive.
    /// </param>
    public static MowerRun Drive(Point start, Point heading, int maxTiles,
        Func<Point, bool> ground, Func<Point, string?> occupant,
        Func<Point, string, (int Damage, bool Killed)> strike, Random rng)
    {
        var run = new MowerRun();
        var at = start;
        var dir = heading;

        for (int travelled = 0; travelled < maxTiles; travelled++)
        {
            // Drift first, so the sideways step and the forward one land the
            // machine on the diagonal neighbour — it wanders across a lane and
            // keeps going, rather than stopping to turn.
            if (travelled >= StraightTiles && rng.NextDouble() < DriftChance)
            {
                var sideways = Perpendicular(dir, rng);
                var beside = new Point(at.X + sideways.X, at.Y + sideways.Y);
                if (ground(beside))
                {
                    at = beside;
                    run.Beats.Add(new MowerBeat(at, MowerStep.Drifted));
                }
            }

            var next = new Point(at.X + dir.X, at.Y + dir.Y);
            // off the edge of the world: it stops there and goes up
            if (!ground(next)) break;
            at = next;

            if (occupant(at) is not string who)
            {
                run.Beats.Add(new MowerBeat(at, MowerStep.Rolled));
                continue;
            }

            var (dealt, killed) = strike(at, who);
            run.Beats.Add(new MowerBeat(at,
                killed ? MowerStep.Through : MowerStep.Bounced, who, dealt));

            if (rng.NextDouble() < ExplodeChance)
            {
                run.Finish(at);
                return run;
            }
            // it drives straight through what it kills; anything still standing
            // knocks it off in some direction nobody chose
            if (!killed) dir = Bounces[rng.Next(Bounces.Length)];
        }

        run.Finish(at);
        return run;
    }

    /// <summary>A step at right angles to the heading, one way or the other.</summary>
    private static Point Perpendicular(Point dir, Random rng)
    {
        int side = rng.Next(2) == 0 ? 1 : -1;
        // turning a heading 90 degrees: (x, y) -> (-y, x), then pick a side
        return new Point(-dir.Y * side, dir.X * side);
    }

    private void Finish(Point at)
    {
        Blast = at;
        Beats.Add(new MowerBeat(at, MowerStep.Exploded));
    }

    /// <summary>Squares the mower actually passed over, in order, without repeats.</summary>
    public IEnumerable<Point> Path => Beats.Select(b => b.Tile);
}
