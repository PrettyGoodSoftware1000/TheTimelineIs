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
    // ---------------- input ----------------

    private void HandleClicks()
    {
        if (_tap is not Point press) return;
        _tap = null;

        if (_mode is Mode.PlayerTurn or Mode.PlayerTarget && HandleCardClick(press)) return;
        if (HitButton(press)) return;

        // Everything on the board is resolved by SQUARE, never by which sprite
        // the cursor happens to be over. The yellow square under the cursor is
        // what a click acts on, so what you see marked is what you get — and
        // somebody standing behind a tree or under a taller neighbour is
        // reachable, which clicking sprites could never manage. This used to be
        // what Ctrl did; now it is simply how clicking works, and Ctrl is left
        // to fade the board so the grid reads clearly.
        // a tap lands where the pointer is, so the board is asked in art
        // pixels rather than in the design space the HUD uses
        if (FindTileAt(_worldPointer) is Point square) ClickSquare(square);
    }

    /// <summary>
    /// Clicking a character out of combat, with the modifier keys behaving the
    /// way they do when picking files:
    ///
    /// - plain click replaces the selection with this one
    /// - shift adds to it
    /// - ctrl adds this one, or drops it if it was already picked
    /// </summary>
    private void PickCharacter(CharacterInstance who)
    {
        if (_ctrl)
        {
            if (!_picked.Remove(who)) _picked.Add(who);
            return;
        }
        if (_shift)
        {
            if (!_picked.Contains(who)) _picked.Add(who);
            return;
        }
        _picked.Clear();
        _picked.Add(who);
    }

    /// <summary>
    /// A click resolved by square — the one the yellow cursor is sitting on.
    /// With a card up it plays on whoever stands there; without one it is a
    /// move, or a selection when a party member is on the square.
    ///
    /// This is how every board click works now. It used to need Ctrl held, and
    /// a plain click hunted for a sprite under the cursor instead — which meant
    /// aiming at a head rather than at a square, and left anyone behind a tree
    /// or under a taller neighbour unclickable.
    /// </summary>
    private void ClickSquare(Point tile)
    {
        var who = WhoIsOn(tile);

        if (_mode == Mode.PlayerTarget && _selectedCard is Card aiming)
        {
            if (aiming.TargetsGround) { TryTargetGround(tile); return; }
            if (who == null) { Toast(_ctx.Strings.Get("iso_empty_square")); return; }
            if (aiming.TargetsAnyone)
            {
                if (who != Acting) TryTarget(who);
                else Toast(_ctx.Strings.Get("iso_needs_other"));
                return;
            }
            // with Friendly Fire this lets both sides through, so a card that
            // does not care whose side it hits can be pointed at your own
            if (MayTarget(Acting, aiming, who)) TryTarget(who);
            else Toast(_ctx.Strings.Get(aiming.TargetsAllies ? "iso_needs_ally" : "iso_needs_enemy"));
            return;
        }

        if (who is { IsPlayer: true })
        {
            if (_mode == Mode.Explore) { PickCharacter(who); _overlayKey = null; }
            else if (_mode is Mode.PlayerTurn or Mode.PlayerTarget) TakeControlOf(who);
            return;
        }

        HandleTileClick(tile);
    }

    /// <summary>
    /// Opens any door this character is now standing next to.
    ///
    /// Walking up to a door is the whole interaction. It used to want a click,
    /// from within two squares, which meant knowing there was a door there
    /// before you could see the room it opened onto.
    /// </summary>
    private void OpenDoorsBeside(CharacterInstance who)
    {
        foreach (var square in who.Footprint.SelectMany(LevelData.Beside).Distinct().ToList())
            if (_level.DoorAt(square) is { Open: false } door &&
                _level.RoomsBeside(square).Count >= 2)
                OpenDoor(door);
    }

    /// <summary>
    /// Opens a doorway and reveals the rooms on both sides of it. The rooms are
    /// read off the squares beside the door rather than stored on it, and the
    /// whole run of touching doorway squares opens together — a two-square gap
    /// is one door, not two.
    /// </summary>
    private void OpenDoor(LevelDoor door)
    {
        var group = _level.DoorGroup(door);
        foreach (var d in group)
        {
            d.Open = true;
            foreach (string room in _level.RoomsBeside(d.Tile))
                _revealed.Add(room);
        }
        _overlayKey = null;
        Log(_ctx.Strings.Get("iso_door_open"));
        var nearest = LivingParty.OrderBy(p => group.Min(d => p.DistanceTo(d.Tile))).First();
        CheckAggro(nearest);
    }

    private void HandleTileClick(Point tile)
    {
        var mover = ActiveMover;
        if (mover == null || !mover.Alive) return;
        // a card spends the turn's movement, but Nimble hands some back — so the
        // gate is the points on hand, never "has a card been played yet"
        if (mover.IsChannelling)
        {
            Toast(_ctx.Strings.Format("iso_channel_rooted", ("card", mover.ChannellingCard)));
            return;
        }
        if (_mode is Mode.PlayerTurn or Mode.PlayerTarget && mover.MovePoints <= 0)
        {
            Toast(_ctx.Strings.Get("iso_move_spent"));
            return;
        }
        // Out of combat the whole selection walks. Everybody heads for the free
        // square nearest where you clicked, so a click never simply refuses
        // because four people will not fit on one tile.
        if (_mode == Mode.Explore && _picked.Count > 1)
        {
            MarchTo(tile);
            return;
        }

        if (!_moveSet.TryGetValue(tile, out int spent)) return;

        if (_mode != Mode.Explore) mover.MovePoints -= spent;
        BeginWalk(mover, tile, null);
    }

    /// <summary>
    /// Sends everybody picked towards one square. The nearest walks onto it and
    /// the rest take the closest free ground they can reach, so a group move
    /// always happens rather than being refused for want of room.
    ///
    /// They are walked one after another — the engine animates one walker at a
    /// time — with each leg starting when the one before it lands.
    /// </summary>
    private void MarchTo(Point goal)
    {
        var going = _picked.Where(p => p.Alive).ToList();
        if (going.Count == 0) return;

        // nearest first, so the one already closest gets the square itself
        var order = going.OrderBy(p => p.DistanceTo(goal)).ToList();
        var claimed = new HashSet<Point>();
        var legs = new List<(CharacterInstance Who, Point Where)>();
        foreach (var who in order)
        {
            var taken = OccupiedExcept(who);
            foreach (var c in claimed) taken.Add(c);
            var spot = _level.Blocks.Keys
                .Where(t => Pathfinder.Fits(_level, t, who.SizeX, who.SizeY, _revealed, taken))
                .OrderBy(t => IsoMath.GridDistance(t, goal))
                .ThenBy(t => IsoMath.GridDistance(t, Tile(who)))
                .Cast<Point?>()
                .FirstOrDefault();
            if (spot is not Point at) continue;
            foreach (var t in Pathfinder.Footprint(at, who.SizeX, who.SizeY)) claimed.Add(t);
            legs.Add((who, at));
        }
        if (legs.Count == 0) return;

        // The first is the walker proper — it runs the clock and the per-step
        // checks that belong to a walk. The rest ride the same clock beside it.
        BeginWalk(legs[0].Who, legs[0].Where, null);
        foreach (var (who, where) in legs.Skip(1))
            AddEscort(who, where);
    }

    /// <summary>
    /// Puts another body on the current walk. It steps in time with the main
    /// walker rather than after it, which is what makes a group move read as
    /// one movement instead of a queue.
    /// </summary>
    private void AddEscort(CharacterInstance who, Point goal)
    {
        var (_, parent) = Pathfinder.Reachable(_level, Tile(who), 9999, _revealed,
            OccupiedExcept(who), false, PassThroughFor(who), who.SizeX, who.SizeY);
        var path = Pathfinder.PathTo(parent, Tile(who), goal);
        if (path.Count == 0) return;
        Record(ReplayEventKind.Move, who, from: Tile(who), to: goal, amount: path.Count);
        ForgetWhereTheyStood(who);
        _escorts.Add(new Escort { Who = who, Path = path, From = Tile(who) });
    }

    /// <summary>
    /// Starting to walk cancels the grace somebody gets for having been inside
    /// a guard zone when it was planted. Their first step onto watched ground
    /// draws a volley, wherever that step started.
    /// </summary>
    private void ForgetWhereTheyStood(CharacterInstance mover)
    {
        foreach (var guard in Everyone.Where(g => g.Alive && g.IsGuarding && g != mover))
            guard.Watch.AboutToWalk(Key(mover));
    }

    private void BeginWalk(CharacterInstance mover, Point goal, Action? after, Card? via = null)
    {
        int budget = _mode == Mode.Explore ? 9999 : mover.MoveMax + (via?.LeapBonus ?? 0);
        var (_, parent) = Pathfinder.Reachable(_level, Tile(mover), budget, _revealed,
            OccupiedExcept(mover), via?.IgnoresHeight ?? false, PassThroughFor(mover), mover.SizeX, mover.SizeY);
        _walker = mover;
        _walkFrom = Tile(mover);
        ForgetWhereTheyStood(mover);
        _escorts.Clear();          // a new walk is not the old one's group
        _walkPath = Pathfinder.PathTo(parent, _walkFrom, goal);
        if (_walkPath.Count > 0)
            Record(ReplayEventKind.Move, mover, from: _walkFrom, to: goal,
                amount: _walkPath.Count);
        _walkT = 0f;
        _walkPause = 0f;
        _afterWalk = after;
        _overlayKey = null;
    }

    /// <summary>
    /// Hands the turn's controls to one of the characters sharing it — the
    /// summoner or one of its pets. Clicking anybody else does nothing, since
    /// they do not act on this turn.
    /// </summary>
    private void TakeControlOf(CharacterInstance who)
    {
        if (!ActsWith(who, Current)) return;
        _petControl = who == Current ? null : who;
        CancelCard();
        _hand = HandOf(ActiveMover ?? who);
        _overlayKey = null;
    }

    /// <summary>
    /// Plays the card in a numbered slot, exactly as clicking it would. The
    /// number is what is printed over the card, counting from 1.
    /// </summary>
    private void PlayCardByNumber(int slot)
    {
        if (slot < 1 || slot > _hand.Count) return;
        SelectCard(_hand[slot - 1]);
    }

    private bool HandleCardClick(Point press)
    {
        var rects = HandRects();
        for (int i = 0; i < _hand.Count; i++)
            if (rects[i].Contains(press))
            {
                SelectCard(_hand[i]);
                return true;
            }
        return false;
    }

    /// <summary>
    /// Arming a card, however it was chosen — clicked, or named by its number
    /// key. Everything the two ways in have in common lives here so they cannot
    /// drift apart: the same affordability check, the same channel release, the
    /// same shortcut for a card with nothing to aim at.
    /// </summary>
    private void SelectCard(Card card)
    {
        if (Acting is CharacterInstance holder && holder.ActionPoints < card.ActionCost)
        {
            Toast(_ctx.Strings.Format("iso_no_actions",
                ("cost", card.ActionCost.ToString()),
                ("points", holder.ActionPoints.ToString())));
            return;
        }
        _selectedCard = card;
        _targets.Clear();
        _overlayKey = null;

        // Releasing a channel does NOT ask where to aim. It was aimed on the
        // turn it was started, and it has been on its way ever since — being
        // asked again would be a second decision the caster never got to make.
        if (Acting is CharacterInstance caster && caster.IsChannelling &&
            card.Name.Equals(caster.ChannellingCard, StringComparison.OrdinalIgnoreCase))
        {
            if (caster.ChannelTurnsLeft > 0)
            {
                Toast(_ctx.Strings.Format("iso_channel_waiting",
                    ("card", caster.ChannellingCard),
                    ("turns", caster.ChannelTurnsLeft.ToString())));
                _selectedCard = null;
                return;
            }
            // what it catches is settled now, where it lands, not when it was
            // aimed — anyone who has since moved into the area is under it
            PlayArea(AreaOf(card, Tile(caster), caster.ChannelAim), caster.ChannelAim);
            return;
        }

        // A pure self-cast (changing shape, planting your feet) has nothing to
        // aim at. A summon is the exception: it acts on the caster, but WHERE
        // the creature lands is the player's call, so it still asks.
        //
        // A guard card goes off immediately even though it carries a damage
        // number, because that number is what the ground does to whoever walks
        // onto it later — there is nobody to point at now.
        if (card.IsGuard || card.IsBathSalts || (card.Damage <= 0 && !card.IsSummon &&
            card.Effects.All(e => Data.Effects.IsSelfCast(e.Name))))
        {
            PlayCard(new List<CharacterInstance>(), Tile(Acting!));
            return;
        }
        _mode = Mode.PlayerTarget;
    }

    /// <summary>
    /// One click per target. A single-target card fires on that click; a card
    /// wanting several collects one per click and fires on the last one.
    /// </summary>
    private void TryTarget(CharacterInstance enemy)
    {
        var card = _selectedCard!;
        var me = Acting!;
        int wanted = TargetsWanted(card);
        if (_targets.Contains(enemy)) return;   // already picked; ignore the repeat

        _targets.Add(enemy);
        if (BestApproach(me, _targets, card) == null)
        {
            _targets.Remove(enemy);
            Toast(_ctx.Strings.Get("iso_out_of_range"));
            return;
        }
        if (_targets.Count < wanted)
        {
            Toast(_ctx.Strings.Format("iso_pick_more", ("count", (wanted - _targets.Count).ToString())));
            _overlayKey = null;
            return;
        }
        Commit(me, card);
    }

    /// <summary>
    /// Ground aiming, for blasts and cones: one click fires at that tile, and
    /// anything the purple outline covers is hit.
    /// </summary>
    private void TryTargetGround(Point tile)
    {
        var card = _selectedCard!;
        var me = Acting!;
        if (!ReachableAim(me, tile, card)) { Toast(_ctx.Strings.Get("iso_out_of_range")); return; }
        // a creature needs somewhere to stand: say so on the spot rather than
        // spending the points and quietly putting it somewhere else
        if (card.IsSummon && !SummonFits(card, tile))
        {
            Toast(_ctx.Strings.Format("iso_summon_no_room", ("name", card.Summons)));
            return;
        }
        PlayArea(AreaOf(card, Tile(me), tile), tile);
    }

    /// <summary>Whether the creature a card summons has room to stand on that square.</summary>
    private bool SummonFits(Card card, Point at)
    {
        var body = _ctx.Classes.Get(card.Summons);
        return Pathfinder.Fits(_level, at, body?.SizeX ?? 1, body?.SizeY ?? 1,
            _revealed, OccupiedExcept(null));
    }

    /// <summary>
    /// Who a card is allowed to touch. Normally the other side; with Friendly
    /// Fire it is everyone standing there, the caster's own team included.
    ///
    /// Sides are read from the CASTER, not from the player, so an enemy's
    /// friendly-fire blast catches the goblin next to it exactly the way one
    /// of ours catches the Cyborg.
    /// </summary>
    private IEnumerable<CharacterInstance> CatchableBy(CharacterInstance? caster, Card card)
    {
        if (card.FriendlyFire)
            return LivingParty.Concat(VisibleEnemies);
        bool casterIsPlayer = caster?.IsPlayer ?? true;
        bool wantsPlayers = card.TargetsAllies == casterIsPlayer;
        return wantsPlayers ? LivingParty : VisibleEnemies;
    }

    /// <summary>Whether this card may be aimed at that character, given who is casting.</summary>
    private bool MayTarget(CharacterInstance? caster, Card card, CharacterInstance who) =>
        CatchableBy(caster, card).Contains(who);

    private void PlayArea(HashSet<Point> area, Point aim)
    {
        var card = _selectedCard;
        if (card == null) return;
        // A summon paints a square to stand on, not a blast: it hits nobody,
        // however many people happen to be near where the creature lands. A
        // mower catches people too, but only once it has driven into them —
        // who that turns out to be is settled by the run, not by the aim.
        var caught = card.IsSummon || card.IsMower
            ? new List<CharacterInstance>()
            : CatchableBy(Acting, card).Where(c => c.Footprint.Any(area.Contains)).ToList();
        // the ground the card covered is remembered here, because by the time
        // the hits resolve the aim and the area are gone
        _burnArea = card.FireTileTurns > 0 ? new HashSet<Point>(area) : new HashSet<Point>();
        _skyTarget = aim;
        PlayCard(caught, aim);
    }

    /// <summary>Sets ground alight, or tops up a square that is already burning.</summary>
    private void LightFires(IEnumerable<Point> tiles, int turns, StringBuilder report)
    {
        int lit = 0;
        foreach (var tile in tiles)
        {
            if (_level.BlockAt(tile) == null) continue;   // no ground, nothing to burn
            _fires.TryGetValue(tile, out int already);
            _fires[tile] = Math.Max(already, turns);
            lit++;
        }
        if (lit > 0)
            report.AppendLine(_ctx.Strings.Format("iso_fire_lit",
                ("count", lit.ToString()), ("turns", turns.ToString())));
    }

    /// <summary>How many bodies this card needs clicked before it can fire.</summary>
    private int TargetsWanted(Card card) => card.Kind == CardKind.MultiTarget
        ? Math.Max(1, Math.Min(card.Targets, CatchableBy(Acting, card).Count()))
        : 1;

    private void Commit(CharacterInstance me, Card card)
    {
        var square = BestApproach(me, _targets, card);
        if (square == null) { Toast(_ctx.Strings.Get("iso_out_of_range")); return; }

        var aimTile = Tile(_targets[0]);
        var shots = _targets.ToList();
        if (square.Value == Tile(me))
        {
            PlayCard(shots, aimTile);
            return;
        }
        me.MovePoints -= _moveSet.TryGetValue(square.Value, out int c) ? c : 0;
        BeginWalk(me, square.Value, () => PlayCard(shots, aimTile), card);
    }

    private bool HitButton(Point press)
    {
        switch (_mode)
        {
            case Mode.PlayerTurn or Mode.PlayerTarget when EndTurnRect.Contains(press):
                NextTurn();
                return true;
        }
        return false;
    }

    /// <summary>Whether the record button is on screen, so drawing and clicking agree.</summary>
    private bool ReplayButtonUp => !_replayMode && _mode != Mode.Victory;
}
