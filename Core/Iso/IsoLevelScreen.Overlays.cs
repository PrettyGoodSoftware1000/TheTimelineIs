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
    // ---------------- overlays ----------------

    /// <summary>
    /// Blue = where the selected character can walk. Red = how far the armed
    /// card reaches from where that character is standing right now — not from
    /// everywhere it could walk to first — and red replaces blue while it shows.
    /// </summary>
    private void RefreshOverlays()
    {
        var mover = ActiveMover;
        var card = _selectedCard ?? HoveredCard();
        var key = (mover, mover == null ? default : Tile(mover), mover?.MovePoints ?? 0,
            card, _revealed.Count, _targets.Count);
        if (Equals(_overlayKey, key)) return;
        _overlayKey = key;

        _moveSet = new Dictionary<Point, int>();
        _rangeSet = new HashSet<Point>();
        _cardArmed = false;
        if (mover == null) return;
        _cardArmed = card != null && _mode != Mode.Explore;

        // a Leap card reaches further and vaults terrain while closing in
        int budget = _mode == Mode.Explore ? 9999 : mover.MovePoints + (card?.LeapBonus ?? 0);
        _moveSet = Pathfinder.Reachable(_level, Tile(mover), budget, _revealed,
            OccupiedExcept(mover), card?.IgnoresHeight ?? false, PassThroughFor(mover),
            mover.SizeX, mover.SizeY).Cost;
        if (card == null || _mode == Mode.Explore) return;

        // a Leap card's reach covers a lot of ground, so it gets its own,
        // lighter wash rather than drowning the level in red
        _rangeOpacityKey = card.LeapBonus > 0 ? "Leap" : "Range";

        // a cone is shown by the purple wedge that follows the cursor, so a red
        // diamond around it would only be a second, wrong-shaped answer
        if (card.Delivery == Delivery.Cone) return;

        // A Leap card carries its own approach: the reach it advertises is the
        // card's range measured from anywhere the leap can put the caster, not
        // from the tile they happen to be standing on.
        var here = Tile(mover);
        var stands = card.LeapBonus > 0
            ? _moveSet.Keys.Append(here).ToList()
            : new List<Point> { here };
        // a card that plants its caster shows the ground it will WATCH, which
        // is measured by the Guard amount rather than by how far the card
        // reaches — the same number the zone itself is built from
        int reach = card.IsGuard ? card.GuardReach : card.Range;
        foreach (var block in _level.Blocks.Values)
        {
            var tile = new Point(block.X, block.Y);
            if (!_level.Shown(tile, _revealed)) continue;
            if (stands.Any(s => IsoMath.GridDistance(s, tile) <= reach))
                _rangeSet.Add(tile);
        }
    }

    private Card? HoveredCard()
    {
        if (_mode is not (Mode.PlayerTurn or Mode.PlayerTarget)) return null;
        var rects = HandRects();
        for (int i = 0; i < _hand.Count; i++)
            if (rects[i].Contains(_pointer)) return _hand[i];
        return null;
    }

    /// <summary>
    /// The purple area an armed area card would cover, following the cursor.
    /// </summary>
    private void UpdateAim()
    {
        _blastSet = new HashSet<Point>();
        _doomed.Clear();

        // While a channel is open the aim is already fixed, so the purple shows
        // where the shot is going to land instead of following the cursor. It
        // is the only way to see what was committed to a turn ago.
        if (Acting is CharacterInstance held && held.IsChannelling &&
            CardNamed(held.ChannellingCard) is Card waiting)
        {
            _blastOpacityKey = waiting.Delivery == Delivery.Cone ? "Cone" : "AoE";
            _blastSet = AreaOf(waiting, Tile(held), held.ChannelAim);
            MarkDoomed(waiting);
            return;
        }

        var aiming = _selectedCard ?? HoveredCard();
        if (aiming == null || Acting == null || _mode == Mode.Explore) return;

        if (!aiming.TargetsGround)
        {
            // A card aimed at one body marks whoever the yellow square is over,
            // so you can see who you are about to hit before you commit — and
            // see nothing when the square is empty.
            MarkDoomed(aiming);
            return;
        }

        _blastOpacityKey = aiming.Delivery == Delivery.Cone ? "Cone" : "AoE";

        if (FindTileAt(_worldPointer) is Point c && ReachableAim(Acting, c, aiming))
        {
            // For a summon the purple is the creature's own outline, and it is
            // only drawn where the creature will actually go. Painting it over
            // a square the body cannot fit on would promise a placement the
            // click then refuses, which reads as a bug.
            if (aiming.IsSummon && !SummonFits(aiming, c)) return;
            _blastSet = AreaOf(aiming, Tile(Acting), c);
        }
        MarkDoomed(aiming);
    }

    /// <summary>
    /// Everyone this card would hit if it went off right now, so they can be
    /// outlined in red before anybody commits to anything.
    ///
    /// It asks the same question the card itself asks when it lands —
    /// CatchableBy — so Friendly Fire is answered once, in one place. Turn the
    /// field on and your own people light up too, because they really are
    /// about to be hit.
    /// </summary>
    private void MarkDoomed(Card card)
    {
        // a summon puts a creature down; there is nobody to hurt
        if (card.IsSummon) return;

        var reachable = CatchableBy(Acting, card).Where(c => c.Alive);
        if (_blastSet.Count > 0)
        {
            foreach (var c in reachable.Where(c => c.Footprint.Any(_blastSet.Contains)))
                _doomed.Add(c);
            return;
        }
        // no area: it is whoever the cursor is sitting on, plus anybody already
        // chosen for a card that wants several
        foreach (var c in _targets) _doomed.Add(c);
        if (card.TargetsGround) return;
        if (FindTileAt(_worldPointer) is Point tile && WhoIsOn(tile) is CharacterInstance who
            && reachable.Contains(who) && ReachableAim(Acting!, tile, card))
            _doomed.Add(who);
    }

    /// <summary>Whether this character already has one of these on the board.</summary>
    private bool SummonAlive(CharacterInstance owner, string what) =>
        what.Length > 0 && _party.Any(p => p.Alive && p.Owner == owner &&
            p.Name.Equals(what, StringComparison.OrdinalIgnoreCase));

    /// <summary>The card of that name from whichever deck the holder draws from.</summary>
    private Card? CardNamed(string name) =>
        _hand.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? _ctx.Cards.All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? _ctx.EnemyCards.All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The tiles a card's area covers: a cone from the caster, or a blast radius.</summary>
    private HashSet<Point> AreaOf(Card card, Point from, Point aim)
    {
        // a summon paints the shape of the thing being summoned, so a body two
        // squares long shows both squares before it is committed to
        if (card.IsSummon)
        {
            var body = _ctx.Classes.Get(card.Summons);
            return new HashSet<Point>(Pathfinder.Footprint(aim,
                body?.SizeX ?? 1, body?.SizeY ?? 1));
        }

        // A mower shows the lane it is being pointed down: straight, no
        // diagonals, as far as it could go. Where it ACTUALLY ends up is
        // another matter — it wanders, and it bounces — but the lane is the
        // decision the player is being asked to make.
        if (card.IsMower)
        {
            var lane = new HashSet<Point>();
            var step = MowerRun.HeadingToward(from, aim);
            var at = from;
            for (int i = 0; i < card.MowerTiles; i++)
            {
                at = new Point(at.X + step.X, at.Y + step.Y);
                if (!_level.Shown(at, _revealed)) break;
                lane.Add(at);
            }
            return lane;
        }

        var set = new HashSet<Point>();
        foreach (var block in _level.Blocks.Values)
        {
            var tile = new Point(block.X, block.Y);
            if (!_level.Shown(tile, _revealed)) continue;
            bool hit = card.Delivery == Delivery.Cone
                ? IsoMath.InCone(from, aim, tile, card.Range)
                : IsoMath.GridDistance(tile, aim) <= card.ExplosionRange;
            if (hit) set.Add(tile);
        }
        return set;
    }

    /// <summary>
    /// Can the card be aimed at this tile? A cone only takes a heading from the
    /// aim point — its own Range caps how far the wedge runs — so any tile will
    /// do. Everything else has to be within reach of where the caster stands.
    /// </summary>
    private bool ReachableAim(CharacterInstance me, Point aim, Card card) =>
        card.Delivery == Delivery.Cone || me.DistanceTo(aim) <= card.Range;

    /// <summary>
    /// Where the caster acts from: where it already stands if that works, else
    /// the cheapest square it can afford with every chosen target in reach. The
    /// player never picks the angle — melee just closes in by the shortest walk.
    /// </summary>
    private Point? BestApproach(CharacterInstance me, List<CharacterInstance> targets, Card card)
    {
        var here = Tile(me);
        if (card.Delivery == Delivery.Cone) return here;
        // measured to the nearest part of the target, so a four-tile body is in
        // reach of anything standing against any of its sides
        bool InRange(Point from) => targets.All(t => t.DistanceTo(from) <= card.Range);
        if (InRange(here)) return here;
        return _moveSet.Keys.Where(InRange)
            .OrderBy(t => _moveSet[t]).Select(t => (Point?)t).FirstOrDefault();
    }
}
