using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Pixel;

namespace TheTimelineIs.Core.Iso;

public partial class IsoLevelScreen
{
    // ---------------- portals ----------------

    /// <summary>
    /// A pair of squares joined to each other. Step on either one and you come
    /// out beside the other.
    /// </summary>
    private sealed class Gate
    {
        public Point A, B;

        /// <summary>The other end of this pair, or null when the square is neither end.</summary>
        public Point? Far(Point at) =>
            at == A ? B : at == B ? (Point?)A : null;
    }

    private readonly List<Gate> _gates = new();

    /// <summary>
    /// Where a portal puts you down, in the order the squares are tried:
    /// south-east of the far end first, then south, south-west, west and east.
    /// If all five are taken, whatever else is beside it will do — arriving
    /// round the back beats not arriving at all.
    ///
    /// Never the far end itself: landing on it would send you straight back
    /// and the pair would bounce you forever.
    ///
    /// These are compass directions as the player SEES them. The grid is
    /// turned 45 degrees on screen, so south-east is +X and south is +X+Y —
    /// which is what Facing8.GridStep answers, rather than this working it
    /// out again.
    /// </summary>
    private static readonly Pixel.Facing8[] GateExits =
    {
        Pixel.Facing8.SouthEast, Pixel.Facing8.South, Pixel.Facing8.SouthWest,
        Pixel.Facing8.West, Pixel.Facing8.East,
        Pixel.Facing8.NorthEast, Pixel.Facing8.NorthWest, Pixel.Facing8.North,
    };

    private static IEnumerable<Point> ExitsAround(Point far) =>
        GateExits.Select(f => far + f.GridStep());

    /// <summary>
    /// Drops a pair of portals on the two squares the card was aimed at.
    ///
    /// Both ends are placed at once, from the card's own aims, so the player
    /// sees the whole thing before anything can walk into it.
    ///
    /// TWO PORTALS, NOT FOUR. Casting it again moves the pair rather than
    /// adding a second one: the card is one doorway with two ends, and a board
    /// with four of them is a board where "step here, come out there" stops
    /// having one answer.
    /// </summary>
    private void OpenGate(IReadOnlyList<Point> aims, StringBuilder report)
    {
        if (aims.Count < 2)
        {
            _ctx.ReportProblem(CardLibrary.PlayerPath,
                "a Gate card needs 'Aims: 2' so the player can place both ends");
            return;
        }
        var (a, b) = (aims[0], aims[1]);
        if (a == b) return;
        _gates.Clear();
        _gates.Add(new Gate { A = a, B = b });
        report.AppendLine(_ctx.Strings.Get("iso_gate_opened"));
    }

    /// <summary>Whether a square is one end of a portal, for the drawing.</summary>
    private bool IsGateTile(Point tile) => _gates.Any(g => g.Far(tile) != null);

    /// <summary>
    /// Steps a walker through a portal, if they just stood on one.
    ///
    /// Only the party travels this way. The exit square has to be ground the
    /// walker could stand on anyway and has to be free — a portal cannot post
    /// somebody into a wall or on top of a friend — and if it isn't, nothing
    /// happens and the walk carries on as though the square were ordinary.
    /// </summary>
    private bool TakeGate(CharacterInstance walker, Point tile)
    {
        if (!walker.IsPlayer) return false;
        var far = _gates.Select(g => g.Far(tile)).FirstOrDefault(p => p != null);
        if (far is not Point other) return false;

        var taken = OccupiedExcept(walker);
        Point? landing = null;
        foreach (var square in ExitsAround(other))
            if (Pathfinder.Fits(_level, square, walker.SizeX, walker.SizeY, _revealed, taken))
            {
                landing = square;
                break;
            }
        // every square round the far end is a wall, a drop or somebody else:
        // nothing happens and the walk carries on as if this were ordinary ground
        if (landing is not Point exit)
        {
            Toast(_ctx.Strings.Get("iso_gate_blocked"));
            return false;
        }

        walker.GX = exit.X;
        walker.GY = exit.Y;
        walker.Face(other, exit);
        _overlayKey = null;
        Record(ReplayEventKind.Move, walker, from: tile, to: exit);
        Log(_ctx.Strings.Format("iso_gate_used", ("name", walker.Name)));
        return true;
    }
}
