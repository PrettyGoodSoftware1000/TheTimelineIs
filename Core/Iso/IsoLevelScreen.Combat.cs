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
    /// What one blow from this card is worth against this target. A card with a
    /// fixed number always does it; one written as a range rolls. A vulnerable
    /// target turns that roll into its highest value — the extra half is added
    /// afterwards, in ApplyHit, so it applies to fixed damage too.
    /// </summary>
    private int RollDamage(Card card, CharacterInstance target)
    {
        int once = !card.VariableDamage ? card.Damage
            : target.IsVulnerable ? card.Damage
            : Rng.Next(card.DamageMin, card.Damage + 1);
        return once * BlastsOver(target);
    }

    /// <summary>
    /// How many of a salvo's blasts are on top of this character. One for
    /// everything else, so an ordinary card is unaffected. A body covering
    /// several squares counts the worst square it is standing on rather than
    /// adding them up, or a Living Stone would take four times the damage for
    /// being four times the size.
    /// </summary>
    private int BlastsOver(CharacterInstance who) =>
        _overlaps.Count == 0 ? 1
        : Math.Max(1, who.Footprint.Max(t => _overlaps.TryGetValue(t, out int n) ? n : 0));

    /// <summary>
    /// Stepping on a trigger square plays its dialogue, once.
    ///
    /// Once per DIALOGUE, not once per square. Painting the same conversation
    /// across a doorway is the normal way to catch a party however it walks in,
    /// and every one of those squares firing meant hearing the same speech four
    /// times. Every square carrying that name is spent the first time any of
    /// them goes off.
    /// </summary>
    private bool FireTrigger(Point tile)
    {
        if (_level.TriggerAt(tile) is not LevelTrigger trigger || trigger.Fired) return false;
        foreach (var t in _level.Triggers.Where(t =>
                     t.Dialogue.Equals(trigger.Dialogue, StringComparison.OrdinalIgnoreCase)))
            t.Fired = true;
        var lines = _dialogue.Get(trigger.Dialogue);
        if (lines == null || lines.Count == 0)
        {
            _ctx.ReportProblem(DialogueLibrary.PathFor(_level.Name),
                $"trigger at {tile.X},{tile.Y} calls dialogue '{trigger.Dialogue}', which has no lines");
            return false;
        }
        _lines = lines;
        _lineIndex = 0;
        return true;
    }

    private void AdvanceDialogue()
    {
        _lineIndex++;
        if (_lines != null && _lineIndex < _lines.Count) return;
        _lines = null;
        _lineIndex = 0;
        // walking into a fight and into a conversation on the same step is possible
        if (_mode == Mode.Explore)
            foreach (var p in LivingParty)
                if (CheckAggro(p)) break;
    }

    private bool CheckAggro(CharacterInstance mover)
    {
        var seen = VisibleEnemies.Where(e =>
            _party.Any(p => p.Alive && e.DistanceTo(p) <= AggroTiles)).ToList();
        if (seen.Count == 0) return false;

        foreach (var e in seen) _aggroed.Add(e);
        // Being spotted IS the start of the fight: everyone fights from where
        // they were caught. There used to be a "free move" stage between the
        // two, and from the outside it looked like the game had locked up —
        // one character could act and nothing said why the others could not.
        if (_mode is Mode.Explore) StartCombat();
        return true;
    }

    /// <summary>
    /// Points saved up belong to one fight. Cleared when combat opens so a
    /// previous battle cannot bankroll this one, and cleared again when it ends
    /// so the walk to the next fight is not spent hoarding.
    /// </summary>
    private void ClearSavedActions()
    {
        foreach (var c in Everyone)
        {
            c.ResetActionPoints();
            StopGuarding(c);
        }
    }

    /// <summary>Lifts a guard zone: the ground stops being watched and the marks come off.</summary>
    private static void StopGuarding(CharacterInstance c) => c.Watch.Stand_Down();

    private void StartCombat()
    {
        ClearSavedActions();
        _order.Clear();
        var players = LivingParty.Where(p => !p.IsPet).OrderBy(_ => Rng.Next()).ToList();
        var foes = _aggroed.Where(e => e.Alive).OrderBy(_ => Rng.Next()).ToList();
        bool playersFirst = Rng.Next(2) == 0;
        var first = playersFirst ? players : foes;
        var second = playersFirst ? foes : players;
        for (int i = 0; i < Math.Max(first.Count, second.Count); i++)
        {
            if (i < first.Count) _order.Add(first[i]);
            if (i < second.Count) _order.Add(second[i]);
        }
        _turn = -1;
        NextTurn();
    }

    /// <summary>
    /// Runs, using the whole move. The square chosen is the one within reach
    /// that puts the most ground between this character and the nearest thing
    /// on the other side; with nowhere better to stand they simply stay put
    /// and the turn still goes.
    /// </summary>
    private void Flee(CharacterInstance who)
    {
        var enemies = (who.IsPlayer ? _enemies : _party).Where(e => e.Alive).ToList();
        var reach = Pathfinder.Reachable(_level, Tile(who), who.MovePoints, _revealed,
            OccupiedExcept(who), sizeX: who.SizeX, sizeY: who.SizeY).Cost;
        who.MovePoints = 0;
        if (enemies.Count == 0 || reach.Count == 0) { NextTurn(); return; }

        int Nearest(Point square) =>
            enemies.Min(e => Pathfinder.Footprint(square, who.SizeX, who.SizeY)
                .Min(t => IsoMath.GridDistance(t, Tile(e))));

        int here = Nearest(Tile(who));
        var away = reach.Keys.Where(t => Fits(who, t, new HashSet<Point>()))
            .OrderByDescending(Nearest).ThenBy(t => reach[t]).FirstOrDefault();
        if (away == default || Nearest(away) <= here) { NextTurn(); return; }
        BeginWalk(who, away, NextTurn);
    }

    private void NextTurn()
    {
        CancelCard();
        // finishing a turn in the fire catches you, the same as starting one there
        if (Current is CharacterInstance leaving)
        {
            Ignite(leaving);
            // nerve comes back at the end of a turn, a little at a time, so a
            // fright wears off over a few turns rather than lasting the fight
            if (leaving.Alive && leaving.RecoverMind(Rng) is int back && back > 0)
                Log(_ctx.Strings.Format("iso_mind_back",
                    ("name", leaving.Name), ("amount", back.ToString()),
                    ("left", leaving.Mind.ToString())));
        }
        if (PartyWiped) { FinishMission("party down"); _ctx.SwitchTo(new DeathScreen(_ctx)); return; }
        if (!_aggroed.Any(e => e.Alive))
        {
            ClearSavedActions();
            _aggroed.Clear();
            _order.Clear();
            _turn = -1;
            _overlayKey = null;
            if (_enemies.All(e => !e.Alive)) { FinishMission("victory"); _mode = Mode.Victory; return; }
            _mode = Mode.Explore;
            Log(_ctx.Strings.Get("iso_clear"));
            return;
        }

        foreach (var e in _aggroed.Where(e => e.Alive && !_order.Contains(e)))
            _order.Add(e);

        for (int step = 0; step < _order.Count; step++)
        {
            _turn = (_turn + 1) % _order.Count;
            if (_order[_turn].Alive) break;
        }
        // A guard forgets anybody who is no longer standing on the ground, so
        // walking back in is a fresh approach. Somebody who died in the zone is
        // forgotten too, in case their name is reused.
        foreach (var g in Everyone.Where(g => g.IsGuarding))
            foreach (var t in Everyone.Where(t => !t.Alive || !InGuardZone(g, t)))
                g.Watch.Forget(Key(t));

        var current = Current!;
        _replayTurn++;
        Record(ReplayEventKind.Turn, current, amount: current.Hp,
            note: $"{current.Hp}/{current.MaxHp} hp");

        // Standing your ground lasts until your next turn comes round. It cost
        // you the rest of THAT turn's movement; it does not cost you every
        // turn after, so the zone lifts here and you walk again.
        if (current.IsGuarding) StopGuarding(current);
        current.MovePoints = current.MoveMax;
        current.RefreshActionPoints();
        if (current.IsBroken)
            Log(_ctx.Strings.Format("iso_mind_broken", ("name", current.Name)));
        else if (current.IsShaken)
            Log(_ctx.Strings.Format("iso_mind_shaken", ("name", current.Name)));
        AgeFires(current);
        _overlayKey = null;

        // a channelled card roots its caster: no movement until it is released
        if (current.IsChannelling)
        {
            if (current.ChannelTurnsLeft > 0) current.ChannelTurnsLeft--;
            current.MovePoints = 0;
            Log(_ctx.Strings.Format("iso_channelling",
                ("name", current.Name), ("card", current.ChannellingCard)));
        }

        if (!BurnAtTurnStart(current)) { NextTurn(); return; }

        // Stunned: the turn arrives and goes straight past. The points and
        // movement handed out above are spent doing nothing, which is the whole
        // cost of it. Checked after the burn so a stunned character still cooks.
        //
        // It is SHOWN rather than skipped in silence: the camera goes to them
        // and holds for a moment, so a turn that produces no action still reads
        // as somebody's turn rather than as the game having missed one out.
        if (current.IsStunned)
        {
            current.StunTurns--;
            current.MovePoints = 0;
            current.ActionPoints = 0;
            Log(_ctx.Strings.Format("iso_stun_skip",
                ("name", current.Name), ("turns", current.StunTurns.ToString())));
            RecenterOn(current);
            _stunHold = StunHoldSeconds;
            _mode = current.IsPlayer ? Mode.PlayerTurn : Mode.EnemyTurn;
            return;
        }

        // Frightened: the turn is spent running. Movement goes on getting away
        // from whoever is nearest on the other side, and nothing else happens
        // — it is the one status that takes the choice away rather than the
        // means, which is what makes it worth being afraid of.
        if (current.IsAfraid)
        {
            current.FearTurns--;
            current.ActionPoints = 0;
            Log(_ctx.Strings.Format("iso_fear_flees", ("name", current.Name)));
            RecenterOn(current);
            _mode = current.IsPlayer ? Mode.PlayerTurn : Mode.EnemyTurn;
            Flee(current);
            return;
        }

        if (current.IsPlayer)
        {
            // a summoner's turn is also its pets': they get their points and
            // movement now, and the player picks between them by clicking
            _petControl = null;
            foreach (var pet in LivingParty.Where(p => p.Owner == current))
            {
                if (pet.IsGuarding) StopGuarding(pet);
                pet.MovePoints = pet.MoveMax;
                pet.RefreshActionPoints();
            }
            _hand = HandOf(current);
            _mode = Mode.PlayerTurn;
        }
        else
        {
            _mode = Mode.EnemyTurn;
        }
    }

    /// <summary>
    /// Fires age once per round rather than once per character, so a three-turn
    /// fire lasts three rounds however many people are in the fight. The round
    /// is marked by the first character in the order taking their turn.
    /// </summary>
    private void AgeFires(CharacterInstance current)
    {
        if (_fires.Count == 0 || _order.Count == 0 || _order[_turn] != _order.First(o => o.Alive))
            return;
        foreach (var tile in _fires.Keys.ToList())
            if (--_fires[tile] <= 0)
                _fires.Remove(tile);
    }
}
