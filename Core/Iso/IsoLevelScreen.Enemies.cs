using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Pixel;
using TheTimelineIs.Core.Render;
using TheTimelineIs.Core.Screens;

namespace TheTimelineIs.Core.Iso;

public partial class IsoLevelScreen
{
    /// <summary>
    /// What an enemy does with its turn, driven entirely by its cards in
    /// EnemyCards.txt:
    ///   1. A melee card it can actually land this turn wins — it walks at the
    ///      nearest player it can reach and swings.
    ///   2. Otherwise a ranged card: it closes only as far as it must to bring
    ///      the nearest player inside that card's range, and no further.
    ///   3. With no usable attack card — none authored, or the Dirtbag has
    ///      lifted the only one — it cannot attack at all, so it wanders to a
    ///      random square it can reach.
    /// </summary>
    private void EnemyAct()
    {
        var me = Current!;
        // a pet is a legitimate target even when it is the last one standing,
        // so enemies aim at the whole living party — but the mission is lost
        // once the real members are gone
        var players = LivingParty;
        if (PartyWiped) { FinishMission("party down"); _ctx.SwitchTo(new DeathScreen(_ctx)); return; }

        // an enemy is bound by action points exactly as the party is
        var hand = HandOf(me)
            .Where(c => !c.TargetsAllies && c.ActionCost <= me.ActionPoints).ToList();
        var reach = Pathfinder.Reachable(_level, Tile(me), me.MovePoints, _revealed,
            OccupiedExcept(me), sizeX: me.SizeX, sizeY: me.SizeY).Cost;
        var stands = reach.Keys.Append(Tile(me)).ToList();

        // how far a target would be if this enemy's body were anchored on a
        // given square — the whole body counts, not just its corner
        int GapFrom(Point square, CharacterInstance target) =>
            Pathfinder.Footprint(square, me.SizeX, me.SizeY).Min(t => IsoMath.GridDistance(t, Tile(target)));

        // longest reach first within each kind, so a spear beats a fist
        foreach (var card in hand.Where(c => c.Delivery == Delivery.Melee)
                                 .OrderByDescending(c => c.Range)
                                 .Concat(hand.Where(c => c.Delivery != Delivery.Melee)
                                             .OrderByDescending(c => c.Range)))
        {
            // the cheapest square that puts somebody in this card's range
            var shot = players
                .SelectMany(p => stands.Select(sq => (Square: sq, Target: p)))
                .Where(x => GapFrom(x.Square, x.Target) <= card.Range)
                .OrderBy(x => reach.TryGetValue(x.Square, out int c) ? c : 0)
                .ThenBy(x => GapFrom(x.Square, x.Target))
                .Select(x => ((Point, CharacterInstance)?)(x.Square, x.Target))
                .FirstOrDefault();
            if (shot == null) continue;

            var (square, victim) = shot.Value;
            if (square == Tile(me)) { EnemyPlay(me, card, victim); return; }
            me.MovePoints -= reach.TryGetValue(square, out int cost) ? cost : 0;
            var goal = square;
            BeginWalk(me, goal, () => EnemyPlay(me, card, victim));
            return;
        }

        // holding a weapon but out of reach of everyone: close the distance
        // and try again next turn
        if (hand.Count > 0)
        {
            var near = players.OrderBy(p => me.DistanceTo(p)).First();
            int wanted = hand.Max(c => c.Range);
            var goal = Pathfinder.StepToward(_level, Tile(me), Tile(near), me.MovePoints,
                wanted, _revealed, OccupiedExcept(me), out var path, me.SizeX, me.SizeY);
            me.MovePoints = 0;
            if (goal != null && path.Count > 0)
            {
                _walker = me;
                _walkFrom = Tile(me);
                _walkPath = path;
                _walkT = 0f;
                _walkPause = 0f;
                _afterWalk = NextTurn;
                return;
            }
            NextTurn();
            return;
        }

        EnemyWander(me, reach);
    }

    /// <summary>
    /// Nothing to attack with at all — no cards authored, or the Dirtbag is
    /// holding the only one. It cannot fight, so it picks a square inside its
    /// movement range at random and ambles there.
    /// </summary>
    private void EnemyWander(CharacterInstance me, Dictionary<Point, int> reach)
    {
        Log(_ctx.Strings.Format("iso_no_cards", ("name", me.Name)));
        me.MovePoints = 0;
        if (reach.Count == 0) { NextTurn(); return; }
        var where = reach.Keys.ElementAt(Rng.Next(reach.Count));
        BeginWalk(me, where, NextTurn);
    }

    /// <summary>
    /// Enemies fire cards through exactly the same pipeline the party uses, so
    /// hit sequences, projectiles, sounds and effects all behave identically.
    /// </summary>
    private void EnemyPlay(CharacterInstance me, Card card, CharacterInstance victim)
    {
        if (!victim.Alive || me.DistanceTo(victim) > card.Range)
        {
            NextTurn();
            return;
        }
        _selectedCard = card;

        // An area card goes off over the ground its target is standing on and
        // catches everyone the card is allowed to catch — the caster's own side
        // included when Friendly Fire says so.
        //
        // Enemies used to hand PlayCard the single body they had aimed at,
        // whatever the card was, which quietly turned every enemy blast and
        // every enemy cone into a one-target jab and left their Friendly Fire
        // line doing nothing at all.
        if (card.TargetsGround)
        {
            var aim = Tile(victim);
            PlayArea(AreaOf(card, Tile(me), aim), aim);
            return;
        }
        PlayCard(new List<CharacterInstance> { victim }, Tile(victim));
    }
}
