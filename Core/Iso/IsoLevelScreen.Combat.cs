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
        if (!card.VariableDamage) return card.Damage;
        return target.IsVulnerable ? card.Damage : Rng.Next(card.DamageMin, card.Damage + 1);
    }

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

    private void NextTurn()
    {
        CancelCard();
        // finishing a turn in the fire catches you, the same as starting one there
        if (Current is CharacterInstance leaving) Ignite(leaving);
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
