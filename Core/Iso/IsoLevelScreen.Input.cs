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

    private void HandleTileClick(Point tile)
    {
        var mover = ActiveMover;
        if (mover == null || !mover.Alive) return;
        // a card spends the turn's movement, but Nimble hands some back — so the
        // gate is the points on hand, never "has a card been played yet"
        if (mover.IsChannelling)
        {
            AskToDropChannel(mover);
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
        who.Face(Tile(who), path[0]);
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
        {
            // turned before the first step, so the walk starts off facing the
            // way it is going instead of swinging round after a square
            mover.Face(_walkFrom, _walkPath[0]);
            Record(ReplayEventKind.Move, mover, from: _walkFrom, to: goal,
                amount: _walkPath.Count);
        }
        _walkT = 0f;
        _walkPause = 0f;
        _afterWalk = after;
        _overlayKey = null;
    }

    /// <summary>
    /// Sets somebody walking along a path that has already been worked out.
    ///
    /// Everything that starts a walk comes through here or through BeginWalk,
    /// and the reason is the first line: TURN FIRST. An enemy closing the
    /// distance used to have its path pushed straight into the fields below,
    /// facing whatever way it happened to be pointed, so a Living Stone set
    /// off walking backwards and only swung round after it had already
    /// crossed a square.
    /// </summary>
    private void StartWalkAlong(CharacterInstance mover, List<Point> path, Action? after)
    {
        _walker = mover;
        _walkFrom = Tile(mover);
        // The same grace BeginWalk cancels, cancelled here too. Without it an
        // enemy that was standing in a guard zone when it went down kept its
        // "already here" mark for the whole walk, so it could cross the wedge
        // untouched — which is the one path enemies closing the distance take.
        ForgetWhereTheyStood(mover);
        _escorts.Clear();
        _walkPath = path;
        if (path.Count > 0) mover.Face(_walkFrom, path[0]);
        _walkT = 0f;
        _walkPause = 0f;
        _afterWalk = after;
        _overlayKey = null;
    }

    /// <summary>
    /// Anything a rooted caster tries to do other than end the turn asks
    /// whether they meant to drop what they are holding. Saying yes costs
    /// them the points they already spent; saying no leaves it running.
    /// </summary>
    private void AskToDropChannel(CharacterInstance who)
    {
        if (Asking) return;
        Ask(_ctx.Strings.Format("iso_stop_channel", ("card", Caps(who.ChannellingCard))),
            () => DropChannel(who));
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
        // A card the holder carries but a pet carries OUT. Picking it hands
        // the controls to that pet first, so everything after this — the
        // reach, the walk in, the action point, the teeth — is the pet's.
        // The Florida Man holds Bite; the Gator is what bites.
        if (card.ActsAs.Length > 0 && Acting is CharacterInstance owner &&
            LivingParty.FirstOrDefault(p => p.Owner == owner &&
                p.Name.Equals(card.ActsAs, StringComparison.OrdinalIgnoreCase)) is CharacterInstance pet)
        {
            TakeControlOf(pet);     // clears any card already armed
        }

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
        // While a channel is open the only thing that card does is offer to
        // let go of it: it aims and fires itself when the wait is up, so
        // there is nothing left to tell it.
        if (Acting is CharacterInstance caster && caster.IsChannelling)
        {
            _selectedCard = null;
            AskToDropChannel(caster);
            return;
        }

        // A pure self-cast (changing shape, planting your feet) has nothing to
        // aim at; see Card.IsSelfCast for what counts.
        //
        // A guard card goes off immediately even though it carries a damage
        // number, because that number is what the ground does to whoever walks
        // onto it later — there is nobody to point at now.
        // An AIMED guard — a cone — is the exception: it has a direction to be
        // given, so it goes through targeting like any other cone.
        if ((card.IsGuard || card.IsLurk) && card.IsAimedGuard)
        {
            _mode = Mode.PlayerTarget;
            return;
        }
        if (card.IsGuard || card.IsLurk || card.IsBathSalts || card.IsSelfCast)
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
        if (_targets.Contains(enemy)) return;   // already picked; ignore the repeat

        // A card that has to come from behind says so before the points are
        // spent, rather than walking somebody round and finding out there.
        if (card.FromBehind && BehindSquares(enemy).All(t => !CanStandOn(me, t)))
        {
            Toast(_ctx.Strings.Get("iso_no_back"));
            return;
        }

        _targets.Add(enemy);
        if (BestApproach(me, _targets, card) == null)
        {
            _targets.Remove(enemy);
            Toast(_ctx.Strings.Get("iso_out_of_range"));
            return;
        }
        // Everything in reach is picked, or the card has all it wants:
        // either way there is nothing left to ask for, so it fires.
        int more = StillWanted(me, card);
        if (more > 0)
        {
            Toast(_ctx.Strings.Format("iso_pick_more", ("count", more.ToString())));
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
        // A salvo picks several squares before anything is fired, the way a
        // multi-target card picks several people. Nothing goes off until the
        // last one is placed, so the whole pattern can be planned.
        if (card.Aims > 1)
        {
            if (_groundAims.Contains(tile)) return;    // already picked; ignore the repeat
            _groundAims.Add(tile);
            _overlayKey = null;
            if (_groundAims.Count < card.Aims)
            {
                Toast(_ctx.Strings.Format("iso_pick_more",
                    ("count", (card.Aims - _groundAims.Count).ToString())));
                return;
            }
            // every blast at once, and where two overlap the damage lands twice
            var salvo = new HashSet<Point>();
            _overlaps.Clear();
            foreach (var at in _groundAims)
                foreach (var t in AreaOf(card, Tile(me), at))
                {
                    salvo.Add(t);
                    _overlaps[t] = _overlaps.TryGetValue(t, out int n) ? n + 1 : 1;
                }
            var centre = _groundAims[0];
            _salvoAims.Clear();
            _salvoAims.AddRange(_groundAims);
            _groundAims.Clear();
            PlayArea(salvo, centre);
            return;
        }
        _salvoAims.Clear();
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
    /// of ours catches the Cyber Smith.
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
        //
        // A GUARD CARD POINTS; IT DOES NOT SHOOT. Its damage is what the
        // watched ground does to whoever walks in on their own turn, so
        // standing in the wedge when it goes down costs nothing. Aiming it at
        // somebody and collecting the damage on the spot made it a cone attack
        // that also left a trap, which is two cards for one action point.
        var caught = card.IsSummon || card.IsMower || card.IsGate ||
                     card.IsGuard || card.IsLurk
            ? new List<CharacterInstance>()
            : CatchableBy(Acting, card).Where(c => c.Footprint.Any(area.Contains)).ToList();
        // the ground the card covered is remembered here, because by the time
        // the hits resolve the aim and the area are gone
        _burnArea = card.FireTileTurns > 0 ? new HashSet<Point>(area) : new HashSet<Point>();
        // The same goes for a gate's two ends. They were read straight off
        // _salvoAims, which the projectile phase empties the moment the shots
        // are in the air — so by the time the effect ran there was nothing
        // left to open a gate between, and the card quietly did nothing.
        _gateAims = card.IsGate ? new List<Point>(_salvoAims) : new List<Point>();
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

    /// <summary>How many bodies this card would like clicked before it fires.</summary>
    private int TargetsWanted(Card card) =>
        card.Kind == CardKind.MultiTarget ? Math.Max(1, card.Targets) : 1;

    /// <summary>
    /// Whether another body could still be added to the ones already picked.
    ///
    /// This used to be guessed at by counting everybody on the board, which
    /// meant a two-target card with three enemies on it and only one of them
    /// reachable wanted two clicks it could never be given: the card simply
    /// stopped working. What matters is not how many are standing about but
    /// how many can actually be hit from somewhere the caster can get to —
    /// and that shrinks as targets are picked, because they all have to be in
    /// reach of the SAME square.
    /// </summary>
    private bool MoreToPick(CharacterInstance me, Card card) =>
        CatchableBy(me, card).Any(c => !_targets.Contains(c)
            && BestApproach(me, _targets.Append(c).ToList(), card) != null);

    /// <summary>How many more clicks this card is still waiting for, if any.</summary>
    private int StillWanted(CharacterInstance me, Card card) =>
        !MoreToPick(me, card) ? 0 : Math.Max(0, TargetsWanted(card) - _targets.Count);

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


}
