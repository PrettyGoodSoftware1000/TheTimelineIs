using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using TheTimelineIs.Core.Data;

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
    /// Where a portal puts you down: the square one step SOUTH-WEST of the far
    /// end, so you arrive beside it rather than on it. Landing ON the far end
    /// would send you straight back and the two would bounce you forever.
    ///
    /// The grid is turned 45 degrees on screen, so +Y is what the player calls
    /// south-west — see <see cref="Pixel.Facings.Towards"/>.
    /// </summary>
    private static Point SouthWestOf(Point tile) => new(tile.X, tile.Y + 1);

    /// <summary>
    /// Drops a pair of portals on the two squares the card was aimed at.
    ///
    /// Both ends are placed at once, from the card's own aims, so the player
    /// sees the whole thing before anything can walk into it. A gate already
    /// using one of these squares is replaced rather than stacked: one portal
    /// per square keeps "step here, come out there" a single answer.
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
        _gates.RemoveAll(g => g.A == a || g.B == a || g.A == b || g.B == b);
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

        var exit = SouthWestOf(other);
        var taken = OccupiedExcept(walker);
        if (!Pathfinder.Fits(_level, exit, walker.SizeX, walker.SizeY, _revealed, taken))
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
