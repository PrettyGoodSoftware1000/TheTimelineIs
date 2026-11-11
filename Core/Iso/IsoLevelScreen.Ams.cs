using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using TheTimelineIs.Core.Data;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// AMS: a ring of covered ground that travels with whoever put it up, and
/// knocks down enemy shots that try to cross it.
///
/// It is NOT a shield on a body. The ring is ground, worked out fresh from
/// where its owner is standing, so walking takes it with you — and a shot at
/// somebody else entirely is stopped if its flight happens to pass through.
///
/// It runs out after <see cref="Effects.AmsTurns"/> of its owner's turns or
/// <see cref="Effects.AmsShots"/> shots, whichever comes first.
/// </summary>
public partial class IsoLevelScreen
{
    /// <summary>Every square an AMS is covering right now, for the border and the test.</summary>
    private HashSet<Point> AmsGround(CharacterInstance who)
    {
        var ground = new HashSet<Point>();
        if (!who.HasAms) return ground;
        foreach (var block in _level.Blocks.Values)
        {
            var tile = new Point(block.X, block.Y);
            if (who.Footprint.Any(f => IsoMath.GridDistance(f, tile) <= who.AmsRadius))
                ground.Add(tile);
        }
        return ground;
    }

    /// <summary>Everybody on the player's side with a ring up.</summary>
    private IEnumerable<CharacterInstance> AmsOperators =>
        LivingParty.Where(p => p.HasAms);

    /// <summary>
    /// The squares a shot passes over on its way from one body to another.
    ///
    /// Sampled along the straight line rather than walked as a path: a shot
    /// flies over whatever is in the way, so where the ground is walkable has
    /// nothing to do with it.
    /// </summary>
    private static IEnumerable<Point> FlightPath(Point from, Point to)
    {
        int steps = Math.Max(1, IsoMath.GridDistance(from, to) * 2);
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            yield return new Point(
                (int)Math.Round(from.X + (to.X - from.X) * t),
                (int)Math.Round(from.Y + (to.Y - from.Y) * t));
        }
    }

    /// <summary>
    /// Whether an AMS knocks this shot down, and spends a shot of its own
    /// doing it.
    ///
    /// Only ENEMY shots are stopped — the ring is pointed outwards — and only
    /// shots, not fists: something that has to walk up and swing was never in
    /// the air to be intercepted.
    /// </summary>
    private bool ShotDown(CharacterInstance? shooter, Card card, CharacterInstance at,
        StringBuilder report)
    {
        if (shooter == null || shooter.IsPlayer) return false;
        if (card.Delivery != Delivery.Ranged) return false;

        var flight = FlightPath(Tile(shooter), Tile(at)).ToHashSet();
        foreach (var guard in AmsOperators)
        {
            var ring = AmsGround(guard);
            if (!flight.Any(ring.Contains)) continue;
            guard.AmsShots--;
            report.AppendLine(_ctx.Strings.Format("iso_ams_stopped",
                ("name", guard.Name), ("card", Caps(card.Name)),
                ("shots", Math.Max(0, guard.AmsShots).ToString())));
            // The third shot is the last one: say so now rather than leaving
            // the player to notice the border gone at the top of a later turn.
            if (!guard.HasAms)
            {
                guard.AmsTurns = guard.AmsShots = guard.AmsRadius = 0;
                report.AppendLine(_ctx.Strings.Format("iso_ams_down", ("name", guard.Name)));
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Takes out of the shot everybody whose part of it was knocked down.
    ///
    /// Run once, as the hits begin, so a card that lands three times spends
    /// one of the AMS's shots rather than three.
    /// </summary>
    private void InterceptShots(Card card, StringBuilder report)
    {
        if (_victims.Count == 0 || !AmsOperators.Any()) return;
        var stopped = _victims
            .Where(v => ShotDown(_actor, card, v, report))
            .ToList();
        foreach (var v in stopped) _victims.Remove(v);
    }

    /// <summary>One turn off the ring's life, at its owner's turn start.</summary>
    private void AgeAms(CharacterInstance who)
    {
        if (who.AmsTurns <= 0) return;
        who.AmsTurns--;
        if (!who.HasAms)
        {
            who.AmsTurns = who.AmsShots = who.AmsRadius = 0;
            Log(_ctx.Strings.Format("iso_ams_down", ("name", who.Name)));
        }
    }
}
