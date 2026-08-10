using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;
using TheTimelineIs.Core.Screens;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// The isometric test level over a black void. The party explores freely and
/// individually; walking within 15 tiles of a revealed enemy springs combat —
/// the rest of the party first gets a free positioning move, then turn order
/// rolls (sides shuffled, alternating).
///
/// Each highlighted region is a colour wash with a border around its outer
/// edge, at the strengths Config.txt gives: blue for where the selected
/// character can walk, red for an armed card's reach, purple for an area. Red
/// replaces blue while a card is up, and none of it shows until somebody is
/// selected.
///
/// One click does everything. Clicking an enemy targets it, and a card that
/// wants several targets fires the moment its last one is clicked; if the
/// caster has to close the distance first it walks there itself. Right-click
/// cancels the armed card.
///
/// Both sides act through the same card pipeline — the party's from
/// PlayerCards.txt, enemies' from EnemyCards.txt — so hit sequences, sounds,
/// projectiles and effects behave identically whoever plays them.
///
/// Nothing about damage is printed over the level. Every blow, burn, theft and
/// turn event goes into the log behind the + button at the top left.
///
/// Stepping on a trigger square plays its dialogue block once.
/// </summary>
public class IsoLevelScreen : IScreen
{
    private enum Mode { Explore, FreeMove, PlayerTurn, PlayerTarget, StealPick, EnemyTurn, Acting, Victory }
    private enum Act { Casting, Projectile, MeleeWait, Hits }

    private const int AggroTiles = 15;

    /// <summary>How much of a sprite survives while Ctrl is held: 80% transparent.</summary>
    private const float CtrlFade = 0.2f;
    private const float WalkTilesPerSec = 5f;
    private const int DoorReach = 2;

    private readonly GameContext _ctx;
    private readonly LevelData _level;
    private readonly DialogueLibrary _dialogue;
    private readonly List<CharacterInstance> _party = new();
    private readonly List<CharacterInstance> _enemies = new();
    private readonly HashSet<string> _revealed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<CharacterInstance> _aggroed = new();
    private readonly List<CharacterInstance> _order = new();
    private static readonly Random Rng = new();

    private Mode _mode = Mode.Explore;
    private int _turn = -1;
    private CharacterInstance? _selected;
    private readonly HashSet<CharacterInstance> _freeMovers = new();
    private List<Card> _hand = new();
    private Card? _selectedCard;
    private readonly List<CharacterInstance> _targets = new();  // chosen targets, in click order
    private HashSet<Point> _blastSet = new();    // purple: tiles the area would cover

    // a steal waiting for the thief to choose what to take
    private CharacterInstance? _stealVictim;
    private int _stealTurns;
    private List<Card> _stealOptions = new();
    private string _stealForm = "";              // set on the shapeshift bonus pick

    // overlays, recomputed only when the mover, position, or card changes
    private Dictionary<Point, int> _moveSet = new();
    private HashSet<Point> _rangeSet = new();
    private bool _cardArmed;          // a card is selected or hovered: red replaces blue
    private string _rangeOpacityKey = "Range";   // "Leap" when the card jumps
    private string _blastOpacityKey = "AoE";     // "Cone" for a wedge
    private object? _overlayKey;

    private Vector2 _camera;
    private Vector2 _baseOrigin;
    private Point _pointer;
    private Point? _tap;
    private bool _ctrl;              // square-targeting mode: see ClickSquare
    private string _toast = "";
    private float _toastTimer;

    // the combat log: every blow, burn and turn event, hidden behind a + button
    private readonly List<string> _log = new();
    private bool _logOpen;
    private int _logScroll;           // lines scrolled back from the newest

    // walking
    private CharacterInstance? _walker;
    private List<Point> _walkPath = new();
    private Point _walkFrom;
    private float _walkT;
    private Action? _afterWalk;

    // card / enemy action timing
    private Act _act;
    private float _actT, _actDur;
    private Card? _actingCard;
    private CharacterInstance? _actor;
    private List<CharacterInstance> _victims = new();
    private int _hitIndex;
    private float _hitTimer;
    private Vector2 _projFrom, _projTo;
    private float _projRotation;

    // dialogue playback
    private List<DialogueLine>? _lines;
    private int _lineIndex;

    // the turn strip starts clear of the log button and runs right
    private static readonly Rectangle TurnStrip = new(220, 34, 2900, 200);
    private const int TurnFaceActive = 190, TurnFace = 130, TurnFaceGap = 18;

    private static readonly Rectangle LogToggleRect = new(60, 40, 96, 96);
    private static readonly Rectangle LogPanel = new(60, 156, 1500, 1180);
    private const float LogTextScale = 0.30f;
    private const int LogLineH = 46;
    private static readonly Rectangle EndTurnRect = new(3280, 60, 500, 160);
    private static readonly Rectangle DoneRect = new(3280, 60, 500, 160);
    private static readonly Rectangle WinRect = new(1620, 1250, 600, 180);
    private static readonly Rectangle DialogueBox = new(60, 1560, 3720, 420);
    private const int CardW = 400, CardH = 560, CardGap = 26;
    private static readonly int CardRestY = VirtualViewport.Height - CardH / 2;
    private const float HoverScale = 1.3f;

    public IsoLevelScreen(GameContext ctx, string levelName)
    {
        _ctx = ctx;
        _level = LevelData.Load(levelName);
        _dialogue = DialogueLibrary.Load(levelName);
        SpawnParty();
        SpawnEnemies();
        foreach (var start in _level.PlayerStarts.Take(_party.Count))
            if (_level.BlockAt(start) is LevelBlock b)
                _revealed.Add(b.Room);
        if (_revealed.Count == 0 && _level.Blocks.Count > 0)
            _revealed.Add(_level.Blocks.Values.First().Room);

        if (_party.Count > 0)
        {
            var focus = IsoMath.ToScreen(_party[0].GX, _party[0].GY, HeightAt(Tile(_party[0])), Vector2.Zero);
            _baseOrigin = new Vector2(VirtualViewport.Width / 2f, VirtualViewport.Height / 2f) - focus;
        }
        Log(_ctx.Strings.Get("iso_enter"));
    }

    private void SpawnParty()
    {
        var names = _ctx.State.PartyOrDefault();
        var starts = _level.PlayerStarts;
        for (int i = 0; i < names.Count; i++)
        {
            var cls = _ctx.Classes.Get(names[i]);
            var at = i < starts.Count ? starts[i]
                : starts.Count > 0 ? new Point(starts[0].X + i, starts[0].Y) : new Point(i, 0);
            _party.Add(new CharacterInstance
            {
                Name = names[i],
                OccurrenceIndex = _party.Count(p => p.Name.Equals(names[i], StringComparison.OrdinalIgnoreCase)),
                IsPlayer = true,
                SpriteFile = cls is { Forms.Count: > 0 }
                    ? cls.SpriteForForm(cls.StartingForm)
                    : PickSprite(cls?.SpriteFiles, names[i], _party),
                Form = cls?.StartingForm ?? "",
                MaxHp = cls?.Hp ?? 20,
                Hp = cls?.Hp ?? 20,
                MoveMax = cls?.Movement ?? 5,
                GX = at.X, GY = at.Y,
            });
        }
    }

    private void SpawnEnemies()
    {
        foreach (var spawn in _level.Enemies)
        {
            var def = _ctx.Enemies.Get(spawn.Name);
            if (def == null) continue;
            _enemies.Add(new CharacterInstance
            {
                Name = def.Name,
                OccurrenceIndex = _enemies.Count(e => e.Name.Equals(def.Name, StringComparison.OrdinalIgnoreCase)),
                IsPlayer = false,
                SpriteFile = PickSprite(def.SpriteFiles, def.Name, _enemies),
                MaxHp = def.Hp,
                Hp = def.Hp,
                MoveMax = def.Movement,
                GX = spawn.X, GY = spawn.Y,
            });
        }
    }

    private static string PickSprite(IReadOnlyList<string>? variants, string name,
        List<CharacterInstance> existing)
    {
        var list = variants is { Count: > 0 } ? variants : new List<string> { $"{name}.png" };
        return list.OrderBy(v => existing.Count(e => e.SpriteFile.Equals(v, StringComparison.OrdinalIgnoreCase)))
                   .First();
    }

    // ---------------- helpers ----------------

    private static Point Tile(CharacterInstance c) => new(c.GX, c.GY);
    private int HeightAt(Point p) => _level.BlockAt(p)?.Height ?? 0;
    private Vector2 Origin => _baseOrigin - _camera;

    private IEnumerable<CharacterInstance> Everyone => _party.Concat(_enemies);
    private List<CharacterInstance> LivingParty => _party.Where(p => p.Alive).ToList();
    private List<CharacterInstance> VisibleEnemies => _enemies.Where(e =>
        e.Alive && _level.BlockAt(Tile(e)) is LevelBlock b && _revealed.Contains(b.Room)).ToList();

    private HashSet<Point> OccupiedExcept(CharacterInstance? except) =>
        Everyone.Where(c => c.Alive && c != except).Select(Tile).ToHashSet();

    /// <summary>
    /// Squares a mover may walk THROUGH but not stop on. Party members squeeze
    /// past each other freely; enemies block the way as before.
    /// </summary>
    private HashSet<Point> PassThroughFor(CharacterInstance mover) => mover.IsPlayer
        ? _party.Where(p => p.Alive && p != mover).Select(Tile).ToHashSet()
        : new HashSet<Point>();

    private CharacterInstance? Current => _turn >= 0 && _turn < _order.Count ? _order[_turn] : null;
    private bool DialogueActive => _lines != null;

    /// <summary>
    /// Records what happened. Everything about damage, burning, shapes and
    /// turns goes here and nowhere else — the log panel is the only place the
    /// player reads it, so the level itself stays uncluttered.
    /// </summary>
    private void Log(string text)
    {
        foreach (var line in text.Split('\n'))
            if (line.Trim().Length > 0)
                _log.Add(line.Trim());
        if (_logScroll > 0) _logScroll++;      // keep the reader's place while pinned back
    }

    /// <summary>
    /// Immediate feedback on something the player just tried to do — out of
    /// range, no movement left. Flashes on screen AND joins the log.
    /// </summary>
    private void Toast(string text)
    {
        _toast = text;
        _toastTimer = 3f;
        Log(text);
    }

    /// <summary>The deck a character's own cards come from.</summary>
    private CardLibrary DeckOf(CharacterInstance c) => c.IsPlayer ? _ctx.Cards : _ctx.EnemyCards;

    /// <summary>Looks a card up by name in whichever deck it came from.</summary>
    private Card? FindCard(string name, bool enemyDeck) =>
        (enemyDeck ? _ctx.EnemyCards : _ctx.Cards).All
            .FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Everything a character can play right now: their own cards, minus any
    /// the Dirtbag is currently holding off them, plus anything they have
    /// stolen from somebody else.
    /// </summary>
    private List<Card> HandOf(CharacterInstance who)
    {
        var tags = who.IsPlayer
            ? _ctx.Classes.CardTagsFor(who.Name)
            : _ctx.Enemies.CardTagsFor(who.Name);
        var hand = who.IsPlayer
            ? DeckOf(who).HandFor(tags, who.Form)
            : DeckOf(who).HandFor(tags);

        // an enemy nobody wrote a card for is dealt the default one, so it can
        // still swing. Once it has one of its own, the default drops away.
        if (!who.IsPlayer && hand.Count == 0)
            hand = DeckOf(who).DefaultHand();

        if (who.Lost.Count > 0)
            hand = hand.Where(c => !who.Lost.Any(l =>
                l.CardName.Equals(c.Name, StringComparison.OrdinalIgnoreCase))).ToList();

        foreach (var loot in who.Stolen)
            if (FindCard(loot.CardName, loot.FromEnemyDeck) is Card borrowed)
                hand.Add(borrowed);
        return hand;
    }

    /// <summary>Gives a borrowed card back and clears it from both sides.</summary>
    private void ReturnStolen(StolenCard loot, CharacterInstance thief)
    {
        thief.Stolen.Remove(loot);
        loot.From?.Lost.Remove(loot);
    }

    /// <summary>Who the current mode lets the player move.</summary>
    private CharacterInstance? ActiveMover => _mode switch
    {
        Mode.Explore => _selected,
        Mode.FreeMove => _selected != null && _freeMovers.Contains(_selected) ? _selected : null,
        Mode.PlayerTurn or Mode.PlayerTarget => Current,
        _ => null,
    };

    private Vector2 FootOf(CharacterInstance c)
    {
        var at = IsoMath.ToScreen(c.GX, c.GY, HeightAt(Tile(c)), Origin);
        if (c == _walker && _walkPath.Count > 0)
        {
            var next = _walkPath[0];
            var from = IsoMath.ToScreen(_walkFrom.X, _walkFrom.Y, HeightAt(_walkFrom), Origin);
            var to = IsoMath.ToScreen(next.X, next.Y, HeightAt(next), Origin);
            at = Vector2.Lerp(from, to, _walkT);
        }
        return at + new Vector2(0, 26) + Recoil.Offset(c);
    }

    // ---------------- update ----------------

    public void Update(InputState input, float dt)
    {
        _pointer = input.PointerPos;
        _tap = input.Tap;
        _ctrl = input.CtrlHeld;
        _camera += input.PanDelta;
        if (_toastTimer > 0) _toastTimer -= dt;
        Recoil.Update(Everyone, dt);
        UpdateCastAnimations(dt);

        if (_tap is Point logTap && LogToggleRect.Contains(logTap))
        {
            _logOpen = !_logOpen;
            _logScroll = 0;
            _tap = null;
            return;
        }
        // the wheel scrolls the log back through history while it is open
        if (_logOpen && LogPanel.Contains(_pointer) && input.ScrollDelta != 0)
            _logScroll = Math.Clamp(_logScroll + input.ScrollDelta,
                0, Math.Max(0, _log.Count - LogLines));

        if (DialogueActive)
        {
            if (_tap.HasValue || input.Confirm) AdvanceDialogue();
            _tap = null;
            return;
        }

        // right-click always drops the armed card
        if (input.AltTap.HasValue) CancelCard();

        if (_walker != null) { UpdateWalk(dt); return; }

        RefreshOverlays();

        switch (_mode)
        {
            case Mode.StealPick: UpdateStealPick(input); break;
            case Mode.Acting: UpdateAction(dt); break;
            case Mode.EnemyTurn: EnemyAct(); break;
            case Mode.Explore:
            case Mode.FreeMove:
            case Mode.PlayerTurn:
            case Mode.PlayerTarget:
                UpdateAim();
                HandleClicks();
                break;
        }
    }

    /// <summary>
    /// Casting animations run on their own clock, at their own frame rate, and
    /// stop when their last frame has been shown — the casting time never
    /// stretches or trims them. A cast shorter than its animation therefore
    /// launches the projectile with the caster still mid-swing, which is what
    /// the art is drawn to do.
    ///
    /// This is ticked before every early return in Update so a cast that
    /// outlives its own phase keeps running through dialogue and walking.
    /// </summary>
    private void UpdateCastAnimations(float dt)
    {
        foreach (var c in Everyone)
        {
            if (c.CastAnim == null) continue;
            c.CastAnimTime += dt;
            if (c.CastAnimTime >= c.CastAnim.Duration)
            {
                c.CastAnim = null;
                c.CastAnimTime = 0f;
            }
        }
    }

    /// <summary>
    /// Swaps the caster's sprite for its casting sheet, from the first frame.
    /// A shapeshifter picks up the sheet for the shape it is wearing right now,
    /// so a card that changes form still casts in the form it started in.
    /// Anyone with no animation declared simply stands there as before.
    /// </summary>
    private void StartCastAnimation(CharacterInstance actor)
    {
        actor.CastAnim = actor.CastAnimationPath is string path
            ? SpriteAnimation.Load(_ctx.Assets, path)
            : null;
        actor.CastAnimTime = 0f;
    }

    private void CancelCard()
    {
        if (_selectedCard == null && _targets.Count == 0) return;
        _selectedCard = null;
        _targets.Clear();
        _blastSet.Clear();
        _overlayKey = null;
        if (_mode == Mode.PlayerTarget) _mode = Mode.PlayerTurn;
    }

    private void UpdateWalk(float dt)
    {
        if (_walker == null) return;
        _walkT += dt * WalkTilesPerSec;
        while (_walkT >= 1f && _walkPath.Count > 0)
        {
            _walkT -= 1f;
            var arrived = _walkPath[0];
            _walkPath.RemoveAt(0);
            _walkFrom = arrived;
            _walker.GX = arrived.X;
            _walker.GY = arrived.Y;
            _overlayKey = null;

            if (_walker.IsPlayer && FireTrigger(arrived)) { _walkPath.Clear(); break; }
            if (_mode == Mode.Explore && _walker.IsPlayer && CheckAggro(_walker))
            {
                _walkPath.Clear();
                break;
            }
        }
        if (_walkPath.Count == 0)
        {
            var done = _afterWalk;
            _walker = null;
            _afterWalk = null;
            done?.Invoke();
        }
    }

    /// <summary>Stepping on a trigger square plays its dialogue, once.</summary>
    private bool FireTrigger(Point tile)
    {
        if (_level.TriggerAt(tile) is not LevelTrigger trigger || trigger.Fired) return false;
        trigger.Fired = true;
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
            _party.Any(p => p.Alive && IsoMath.GridDistance(Tile(p), Tile(e)) <= AggroTiles)).ToList();
        if (seen.Count == 0) return false;

        foreach (var e in seen) _aggroed.Add(e);
        if (_mode is Mode.Explore)
        {
            _freeMovers.Clear();
            foreach (var p in LivingParty.Where(p => p != mover))
            {
                p.MovePoints = p.MoveMax;
                _freeMovers.Add(p);
            }
            _mode = Mode.FreeMove;
            _selected = _freeMovers.FirstOrDefault();
            _overlayKey = null;
            Log(_ctx.Strings.Get("iso_spotted"));
        }
        return true;
    }

    private void StartCombat()
    {
        _order.Clear();
        var players = LivingParty.OrderBy(_ => Rng.Next()).ToList();
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
        if (LivingParty.Count == 0) { _ctx.SwitchTo(new DeathScreen(_ctx)); return; }
        if (!_aggroed.Any(e => e.Alive))
        {
            _aggroed.Clear();
            _order.Clear();
            _turn = -1;
            _overlayKey = null;
            if (_enemies.All(e => !e.Alive)) { _mode = Mode.Victory; return; }
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
        var current = Current!;
        current.MovePoints = current.MoveMax;
        current.RefreshActionPoints();
        _overlayKey = null;
        if (!BurnAtTurnStart(current)) { NextTurn(); return; }
        if (current.IsPlayer)
        {
            _hand = HandOf(current);
            _mode = Mode.PlayerTurn;
        }
        else
        {
            _mode = Mode.EnemyTurn;
        }
    }

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
            OccupiedExcept(mover), card?.IgnoresHeight ?? false, PassThroughFor(mover)).Cost;
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
        foreach (var block in _level.Blocks.Values)
        {
            if (!_revealed.Contains(block.Room)) continue;
            var tile = new Point(block.X, block.Y);
            if (stands.Any(s => IsoMath.GridDistance(s, tile) <= card.Range))
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
        if (_selectedCard is not { TargetsGround: true } card || Current == null) return;
        _blastOpacityKey = card.Delivery == Delivery.Cone ? "Cone" : "AoE";

        if (FindTileAt(_pointer.ToVector2()) is Point c && ReachableAim(Current, c, card))
            _blastSet = AreaOf(card, Tile(Current), c);
    }

    /// <summary>The tiles a card's area covers: a cone from the caster, or a blast radius.</summary>
    private HashSet<Point> AreaOf(Card card, Point from, Point aim)
    {
        var set = new HashSet<Point>();
        foreach (var block in _level.Blocks.Values)
        {
            if (!_revealed.Contains(block.Room)) continue;
            var tile = new Point(block.X, block.Y);
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
        card.Delivery == Delivery.Cone || IsoMath.GridDistance(Tile(me), aim) <= card.Range;

    /// <summary>
    /// Where the caster acts from: where it already stands if that works, else
    /// the cheapest square it can afford with every chosen target in reach. The
    /// player never picks the angle — melee just closes in by the shortest walk.
    /// </summary>
    private Point? BestApproach(CharacterInstance me, List<CharacterInstance> targets, Card card)
    {
        var here = Tile(me);
        if (card.Delivery == Delivery.Cone) return here;
        bool InRange(Point from) => targets.All(t => IsoMath.GridDistance(from, Tile(t)) <= card.Range);
        if (InRange(here)) return here;
        return _moveSet.Keys.Where(InRange)
            .OrderBy(t => _moveSet[t]).Select(t => (Point?)t).FirstOrDefault();
    }

    // ---------------- input ----------------

    private void HandleClicks()
    {
        if (_tap is not Point press) return;
        _tap = null;

        if (_mode is Mode.PlayerTurn or Mode.PlayerTarget && HandleCardClick(press)) return;
        if (HitButton(press)) return;

        // Ctrl aims at the SQUARE rather than at whatever sprite happens to be
        // over it, which is the only way to reach somebody standing behind a
        // tree or under a taller neighbour
        if (_ctrl)
        {
            if (FindTileAt(press.ToVector2()) is Point square) ClickSquare(square);
            return;
        }

        foreach (var c in Everyone.Reverse())
        {
            if (!c.Alive || !SpriteRect(c).Contains(press)) continue;
            if (_mode == Mode.PlayerTarget && _selectedCard is Card aiming)
            {
                if (aiming.TargetsGround) TryTargetGround(Tile(c));
                // stealing works on anyone, so it skips the side check entirely
                else if (aiming.TargetsAnyone && c != Current) TryTarget(c);
                else if (aiming.TargetsAnyone) Toast(_ctx.Strings.Get("iso_needs_other"));
                else if (c.IsPlayer == aiming.TargetsAllies) TryTarget(c);
                else Toast(_ctx.Strings.Get(aiming.TargetsAllies ? "iso_needs_ally" : "iso_needs_enemy"));
                return;
            }
            if (c.IsPlayer)
            {
                if (_mode == Mode.Explore) { _selected = c; _overlayKey = null; }
                else if (_mode == Mode.FreeMove && _freeMovers.Contains(c)) { _selected = c; _overlayKey = null; }
            }
            return;
        }

        if (FindTileAt(press.ToVector2()) is Point tile)
        {
            if (_level.DoorAt(tile) is LevelDoor door && !door.Open && _mode is Mode.Explore &&
                LivingParty.Any(p => IsoMath.GridDistance(Tile(p), tile) <= DoorReach))
            {
                OpenDoor(door);
                return;
            }
            // an area card can be aimed at bare ground, no enemy required
            if (_mode == Mode.PlayerTarget && _selectedCard is { TargetsGround: true })
            {
                TryTargetGround(tile);
                return;
            }
            HandleTileClick(tile);
        }
    }

    /// <summary>
    /// A click resolved by square, under Ctrl. With a card up it plays exactly
    /// as if whoever stands there had been clicked; without one it is a move,
    /// or a selection when a party member is standing on the square.
    /// </summary>
    private void ClickSquare(Point tile)
    {
        var who = Everyone.FirstOrDefault(c => c.Alive && Tile(c) == tile);

        if (_mode == Mode.PlayerTarget && _selectedCard is Card aiming)
        {
            if (aiming.TargetsGround) { TryTargetGround(tile); return; }
            if (who == null) { Toast(_ctx.Strings.Get("iso_empty_square")); return; }
            if (aiming.TargetsAnyone)
            {
                if (who != Current) TryTarget(who);
                else Toast(_ctx.Strings.Get("iso_needs_other"));
                return;
            }
            if (who.IsPlayer == aiming.TargetsAllies) TryTarget(who);
            else Toast(_ctx.Strings.Get(aiming.TargetsAllies ? "iso_needs_ally" : "iso_needs_enemy"));
            return;
        }

        if (who is { IsPlayer: true })
        {
            if (_mode == Mode.Explore) { _selected = who; _overlayKey = null; }
            else if (_mode == Mode.FreeMove && _freeMovers.Contains(who))
            { _selected = who; _overlayKey = null; }
            return;
        }

        if (_level.DoorAt(tile) is LevelDoor d && !d.Open && _mode is Mode.Explore &&
            LivingParty.Any(p => IsoMath.GridDistance(Tile(p), tile) <= DoorReach))
        {
            OpenDoor(d);
            return;
        }
        HandleTileClick(tile);
    }

    private void OpenDoor(LevelDoor door)
    {
        door.Open = true;
        _revealed.Add(door.RoomA);
        _revealed.Add(door.RoomB);
        _overlayKey = null;
        Log(_ctx.Strings.Get("iso_door_open"));
        var nearest = LivingParty.OrderBy(p =>
            IsoMath.GridDistance(Tile(p), new Point(door.X, door.Y))).First();
        CheckAggro(nearest);
    }

    private void HandleTileClick(Point tile)
    {
        var mover = ActiveMover;
        if (mover == null || !mover.Alive) return;
        // a card spends the turn's movement, but Nimble hands some back — so the
        // gate is the points on hand, never "has a card been played yet"
        if (_mode is Mode.PlayerTurn or Mode.PlayerTarget && mover.MovePoints <= 0)
        {
            Toast(_ctx.Strings.Get("iso_move_spent"));
            return;
        }
        if (!_moveSet.TryGetValue(tile, out int spent)) return;

        if (_mode != Mode.Explore) mover.MovePoints -= spent;
        BeginWalk(mover, tile, null);
    }

    private void BeginWalk(CharacterInstance mover, Point goal, Action? after, Card? via = null)
    {
        int budget = _mode == Mode.Explore ? 9999 : mover.MoveMax + (via?.LeapBonus ?? 0);
        var (_, parent) = Pathfinder.Reachable(_level, Tile(mover), budget, _revealed,
            OccupiedExcept(mover), via?.IgnoresHeight ?? false, PassThroughFor(mover));
        _walker = mover;
        _walkFrom = Tile(mover);
        _walkPath = Pathfinder.PathTo(parent, _walkFrom, goal);
        _walkT = 0f;
        _afterWalk = after;
        _overlayKey = null;
    }

    private bool HandleCardClick(Point press)
    {
        var rects = HandRects();
        for (int i = 0; i < _hand.Count; i++)
            if (rects[i].Contains(press))
            {
                if (Current is CharacterInstance holder && holder.ActionPoints < _hand[i].ActionCost)
                {
                    Toast(_ctx.Strings.Format("iso_no_actions",
                        ("cost", _hand[i].ActionCost.ToString()),
                        ("points", holder.ActionPoints.ToString())));
                    return true;
                }
                _selectedCard = _hand[i];
                _targets.Clear();
                _overlayKey = null;
                // a pure self-cast (changing shape) has nothing to aim at
                if (_selectedCard.Damage <= 0 &&
                    _selectedCard.Effects.All(e => Data.Effects.IsSelfCast(e.Name)))
                {
                    PlayCard(new List<CharacterInstance>(), Tile(Current!));
                    return true;
                }
                _mode = Mode.PlayerTarget;
                return true;
            }
        return false;
    }

    /// <summary>
    /// One click per target. A single-target card fires on that click; a card
    /// wanting several collects one per click and fires on the last one.
    /// </summary>
    private void TryTarget(CharacterInstance enemy)
    {
        var card = _selectedCard!;
        var me = Current!;
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
        var me = Current!;
        if (!ReachableAim(me, tile, card)) { Toast(_ctx.Strings.Get("iso_out_of_range")); return; }
        PlayArea(AreaOf(card, Tile(me), tile), tile);
    }

    private void PlayArea(HashSet<Point> area, Point aim)
    {
        var card = _selectedCard;
        if (card == null) return;
        var caught = (card.TargetsAllies ? (IEnumerable<CharacterInstance>)LivingParty : VisibleEnemies)
            .Where(c => area.Contains(Tile(c))).ToList();
        PlayCard(caught, aim);
    }

    /// <summary>How many enemies this card needs clicked before it can fire.</summary>
    private int TargetsWanted(Card card) => card.Kind == CardKind.MultiTarget
        ? Math.Max(1, Math.Min(card.Targets,
            card.TargetsAllies ? LivingParty.Count : VisibleEnemies.Count))
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
            case Mode.FreeMove when DoneRect.Contains(press):
                _freeMovers.Clear();
                StartCombat();
                return true;
            case Mode.PlayerTurn or Mode.PlayerTarget when EndTurnRect.Contains(press):
                NextTurn();
                return true;
        }
        return false;
    }

    // ---------------- card + enemy actions ----------------

    private void PlayCard(List<CharacterInstance> aimed, Point blastCenter)
    {
        var card = _selectedCard;
        if (card == null) return;
        _actor = Current;
        _actingCard = card;
        _victims = aimed;
        _selectedCard = null;
        _targets.Clear();
        _blastSet.Clear();
        // playing a borrowed card uses it up and hands it straight back
        if (_actor != null && _actor.Stolen.FirstOrDefault(st =>
                st.CardName.Equals(card.Name, StringComparison.OrdinalIgnoreCase)) is StolenCard spent)
        {
            ReturnStolen(spent, _actor);
            Log(_ctx.Strings.Format("iso_steal_over",
                ("card", spent.CardName), ("owner", spent.From?.Name ?? "?")));
        }

        _actor!.ActionPoints = Math.Max(0, _actor.ActionPoints - card.ActionCost);
        // changing shape is free of the movement penalty too: a shapeshifter can
        // shift and then still walk, though the shift itself costs its points
        if (card.BecomesForm == null)
            _actor.MovePoints = 0;   // a card ends this turn's movement, unless Nimble gives it back
        _overlayKey = null;

        _ctx.Sounds.Play(card.CastingSound);
        StartCastAnimation(_actor!);
        _mode = Mode.Acting;
        EnterAct(Act.Casting, card.CastingTime ?? _ctx.Sounds.Duration(card.CastingSound));
    }

    private void EnterAct(Act act, float duration)
    {
        _act = act;
        _actT = 0f;
        _actDur = Math.Max(0f, duration);
        if (act == Act.Hits)
            _hitTimer = _actingCard is { HitEvents.Count: > 0 } c ? c.HitEvents[0].Delay : 0f;
    }

    private void UpdateAction(float dt)
    {
        if (_act == Act.Hits) { UpdateHits(dt); return; }

        _actT += dt;
        if (_actDur > 0 && _actT < _actDur) return;

        switch (_act)
        {
            case Act.Casting when _actingCard is { Delivery: Delivery.Ranged } ranged:
                var aim = _victims.FirstOrDefault();
                // a self-cast has nobody to fly at, but its effects still have
                // to resolve — skip the projectile, not the hit phase
                if (aim == null) { _hitIndex = 0; EnterAct(Act.Hits, 0f); return; }
                _projFrom = FootOf(_actor!) - new Vector2(0, 160);
                _projTo = FootOf(aim) - new Vector2(0, 160);
                _projRotation = (float)Math.Atan2(_projTo.Y - _projFrom.Y, _projTo.X - _projFrom.X);
                EnterAct(Act.Projectile,
                    IsoMath.GridDistance(Tile(_actor!), Tile(aim)) / Math.Max(1f, ranged.Speed));
                break;
            case Act.Casting when _actingCard is { Delivery: Delivery.Melee } melee:
                EnterAct(Act.MeleeWait, melee.MeleeTime);
                break;
            case Act.Casting:
            case Act.Projectile:
            case Act.MeleeWait:
                _hitIndex = 0;
                EnterAct(Act.Hits, 0f);
                break;
        }
    }

    private void UpdateHits(float dt)
    {
        _hitTimer -= dt;
        if (_hitTimer > 0f) return;
        var card = _actingCard!;
        var schedule = card.DamageSchedule();
        int dmg = _hitIndex < schedule.Length ? schedule[_hitIndex] : 0;
        _ctx.Sounds.Play(_hitIndex < card.HitEvents.Count ? card.HitEvents[_hitIndex].Sound : null);

        var report = new StringBuilder();
        var struck = _victims.Where(v => v.Alive).ToList();
        foreach (var v in struck)
            // a curse makes every melee blow land harder on its victim
            ApplyHit(v, dmg + (card.Delivery == Delivery.Melee ? v.CurseBonus : 0),
                card.DamageType, report);

        _hitIndex++;
        bool lastBlow = _hitIndex >= card.HitEvents.Count;
        if (lastBlow && card.Effects.Count > 0)
            ApplyEffects(card, struck, report);
        if (report.Length > 0) Log(report.ToString().TrimEnd());

        if (!lastBlow)
        {
            _hitTimer = card.HitEvents[_hitIndex].Delay;
            return;
        }
        FinishAction();
    }

    private void FinishAction()
    {
        _actingCard = null;
        _victims.Clear();
        _overlayKey = null;
        if (LivingParty.Count == 0) { _ctx.SwitchTo(new DeathScreen(_ctx)); return; }

        // a Steal held the thief's choice back until the card finished; make
        // them pick now, before the turn moves on. _actor stays set for it.
        if (_stealVictim != null) { BeginStealPick(); return; }

        _actor = null;
        ResumeAfterAction();
    }

    /// <summary>Back to the turn if anything is left to spend on it, else onward.</summary>
    private void ResumeAfterAction()
    {
        _actor = null;
        var mover = Current;
        if (mover != null && mover.IsPlayer && mover.Alive &&
            (mover.ActionPoints > 0 || mover.MovePoints > 0))
        {
            _mode = Mode.PlayerTurn;
            return;
        }
        NextTurn();
    }

    /// <summary>
    /// Armor is an extension of health: it soaks damage first and only what's
    /// left over reaches hit points, so 6 damage against 5 armor strips the
    /// armor and takes 1 off health.
    /// </summary>
    private void ApplyHit(CharacterInstance target, int dmg, string type, StringBuilder report)
    {
        if (dmg <= 0 || !target.Alive) return;
        target.ShakeTimer = Recoil.Duration;

        int soaked = Math.Min(target.Armor, dmg);
        target.Armor -= soaked;
        int through = dmg - soaked;
        target.Hp -= through;

        report.AppendLine(soaked > 0
            ? _ctx.Strings.Format("iso_hit_armor", ("target", target.Name),
                ("dmg", through.ToString()), ("type", type), ("soaked", soaked.ToString()))
            : _ctx.Strings.Format("battle_hit", ("target", target.Name),
                ("dmg", through.ToString()), ("type", type)));

        if (target.Hp <= 0)
        {
            target.Hp = 0;
            target.Alive = false;
            report.AppendLine(_ctx.Strings.Format("battle_down", ("name", target.Name)));
        }
    }

    /// <summary>Runs a card's Effects against everything it hit.</summary>
    private void ApplyEffects(Card card, IEnumerable<CharacterInstance> hit, StringBuilder report)
    {
        foreach (var effect in card.Effects)
        {
            if (Data.Effects.IsSelfCast(effect.Name))
            {
                if (_actor == null) continue;
                if (effect.Is(Data.Effects.Nimble))
                {
                    // Nimble hands movement back to the caster, not to the victims
                    _actor.MovePoints += effect.Amount;
                    report.AppendLine(_ctx.Strings.Format("iso_nimble",
                        ("name", _actor.Name), ("points", effect.Amount.ToString())));
                }
                else if (effect.Is(Data.Effects.Form))
                {
                    ChangeForm(_actor, effect.Text, report);
                }
                // Leap already did its work when the approach was planned
                continue;
            }
            foreach (var c in hit.Where(c => c.Alive))
            {
                if (effect.Is(Data.Effects.Burning))
                {
                    // each stack starts its own 2-turn life; existing ones are untouched
                    for (int i = 0; i < effect.Amount; i++)
                        c.Burns.Add(Data.Effects.BurnTurns);
                    report.AppendLine(_ctx.Strings.Format("iso_burning",
                        ("name", c.Name), ("stacks", c.BurningStacks.ToString())));
                }
                else if (effect.Is(Data.Effects.Armor))
                {
                    c.Armor += effect.Amount;
                    report.AppendLine(_ctx.Strings.Format("iso_armored",
                        ("name", c.Name), ("armor", c.Armor.ToString())));
                }
                else if (effect.Is(Data.Effects.Curse))
                {
                    // like burning, each curse keeps its own clock
                    c.Curses.Add((effect.Amount, Data.Effects.CurseTurns));
                    report.AppendLine(_ctx.Strings.Format("iso_cursed",
                        ("name", c.Name), ("bonus", c.CurseBonus.ToString())));
                }
                else if (effect.Is(Data.Effects.Steal))
                {
                    StealFrom(c, effect.Amount, report);
                }
            }
        }
    }

    /// <summary>
    /// Lifts one card off the victim — friend or foe — and hands it to the
    /// caster for the next few of their turns. The victim cannot play it while
    /// it is gone, which is how an enemy ends up with nothing to attack with.
    /// A card already stolen from somebody is not stolen again.
    /// </summary>
    private void StealFrom(CharacterInstance victim, int turns, StringBuilder report)
    {
        if (_actor == null || _actor == victim) return;
        var takeable = StealableFrom(victim, _actor);
        if (takeable.Count == 0)
        {
            report.AppendLine(_ctx.Strings.Format("iso_nothing_to_steal", ("name", victim.Name)));
            return;
        }
        // the thief chooses, so the pick waits until the card has finished
        // resolving and FinishAction can hand over to the picker
        _stealVictim = victim;
        _stealTurns = Math.Max(1, turns);
    }

    /// <summary>
    /// What can actually be lifted off somebody: cards that are genuinely
    /// theirs, so not ones they are themselves borrowing, and not the card
    /// being played to steal with.
    /// </summary>
    private List<Card> StealableFrom(CharacterInstance victim, CharacterInstance thief,
        string? asForm = null)
    {
        var borrowed = victim.Stolen
            .Select(st => st.CardName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var already = thief.Stolen
            .Select(st => st.CardName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // asForm looks into a shape the victim is NOT currently wearing, which
        // is how a stolen shapeshift card brings one of that shape's cards along
        var pool = asForm == null
            ? HandOf(victim)
            : DeckOf(victim).HandFor(
                victim.IsPlayer ? _ctx.Classes.CardTagsFor(victim.Name)
                                : _ctx.Enemies.CardTagsFor(victim.Name), asForm);

        return pool
            .Where(c => !borrowed.Contains(c.Name))
            .Where(c => !already.Contains(c.Name))
            .Where(c => c != _actingCard)
            .ToList();
    }

    /// <summary>Opens the picker, or closes the whole business if there is nothing to show.</summary>
    private void BeginStealPick(string? followUpForm = null)
    {
        if (_stealVictim == null || _actor == null) { EndStealPick(); return; }
        _stealOptions = StealableFrom(_stealVictim, _actor, followUpForm);
        _stealForm = followUpForm ?? "";
        if (_stealOptions.Count == 0) { EndStealPick(); return; }
        _mode = Mode.StealPick;
    }

    /// <summary>Takes the chosen card, then offers the shapeshift bonus if it earned one.</summary>
    private void TakeStolen(Card loot)
    {
        var victim = _stealVictim;
        var thief = _actor;
        if (victim == null || thief == null) { EndStealPick(); return; }

        var record = new StolenCard
        {
            CardName = loot.Name,
            From = victim,
            FromEnemyDeck = !victim.IsPlayer,
            TurnsLeft = _stealTurns,
        };
        thief.Stolen.Add(record);
        victim.Lost.Add(record);
        Log(_ctx.Strings.Format("iso_stole",
            ("thief", thief.Name), ("card", loot.Name), ("victim", victim.Name),
            ("turns", record.TurnsLeft.ToString())));

        // the one exception to one-card-per-steal: taking a shapeshift card
        // also lets you reach into the shape it would have turned them into
        if (_stealForm.Length == 0 && loot.BecomesForm is string shape)
        {
            BeginStealPick(shape);
            return;
        }
        EndStealPick();
    }

    private void EndStealPick()
    {
        _stealVictim = null;
        _stealOptions = new List<Card>();
        _stealForm = "";
        _hand = Current != null ? HandOf(Current) : _hand;
        ResumeAfterAction();
    }

    /// <summary>Picker clicks: one card, or right-click / Escape to take nothing.</summary>
    private void UpdateStealPick(InputState input)
    {
        if (input.AltTap.HasValue || input.Cancel) { EndStealPick(); return; }
        if (_tap is not Point press) return;
        _tap = null;
        var rects = StealRects();
        for (int i = 0; i < _stealOptions.Count && i < rects.Count; i++)
            if (rects[i].Contains(press))
            {
                TakeStolen(_stealOptions[i]);
                return;
            }
    }

    private List<Rectangle> StealRects()
    {
        int n = Math.Max(1, _stealOptions.Count);
        int total = n * (CardW + CardGap) - CardGap;
        // narrow the cards rather than run off the screen when a hand is large
        int w = CardW, gap = CardGap;
        if (total > VirtualViewport.Width - 200)
        {
            w = (VirtualViewport.Width - 200 - (n - 1) * gap) / n;
            total = n * (w + gap) - gap;
        }
        int x0 = (VirtualViewport.Width - total) / 2;
        int h = (int)(CardH * (w / (float)CardW));
        var rects = new List<Rectangle>();
        for (int i = 0; i < n; i++)
            rects.Add(new Rectangle(x0 + i * (w + gap), (VirtualViewport.Height - h) / 2, w, h));
        return rects;
    }

    private void DrawStealPick(SpriteBatch batch)
    {
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height), Color.Black * 0.72f);
        Ui.DrawTextCentered(batch, _ctx.Font,
            _stealForm.Length > 0
                ? _ctx.Strings.Format("iso_steal_pick_form", ("form", _stealForm))
                : _ctx.Strings.Format("iso_steal_pick", ("name", _stealVictim?.Name ?? "?")),
            new Rectangle(0, 320, VirtualViewport.Width, 120), Color.Gold, 0.56f);

        var rects = StealRects();
        for (int i = 0; i < _stealOptions.Count && i < rects.Count; i++)
            DrawCard(batch, _stealOptions[i], rects[i], rects[i].Contains(_pointer));
    }

    /// <summary>Swaps a shapeshifter's shape, and with it the cards in its hand.</summary>
    private void ChangeForm(CharacterInstance who, string form, StringBuilder report)
    {
        var cls = _ctx.Classes.Get(who.Name);
        if (cls?.FindForm(form) is not ClassForm target)
        {
            _ctx.ReportProblem(ClassLibrary.Path,
                $"'{who.Name}' has no form called '{form}', so the card could not change shape");
            return;
        }
        who.Form = target.Name;
        who.SpriteFile = target.Sprite;
        if (who == Current)
            _hand = HandOf(who);
        report.AppendLine(_ctx.Strings.Format("iso_form", ("name", who.Name), ("form", target.Name)));
    }
    /// <summary>
    /// Burning bites at the victim's own turn start: every live stack deals its
    /// damage, then each stack ages independently and the spent ones go out.
    /// Returns false if the fire killed them.
    /// </summary>
    private bool BurnAtTurnStart(CharacterInstance c)
    {
        // curses tick down on their victim's turn too, independently of each other
        if (c.Curses.Count > 0)
        {
            for (int i = 0; i < c.Curses.Count; i++)
                c.Curses[i] = (c.Curses[i].Amount, c.Curses[i].Turns - 1);
            c.Curses.RemoveAll(x => x.Turns <= 0);
        }
        // borrowed cards run on the THIEF's clock: the turn they were taken on
        // counts as the first, so Steal 3 is "now, or either of your next two"
        for (int i = c.Stolen.Count - 1; i >= 0; i--)
        {
            var loot = c.Stolen[i];
            if (--loot.TurnsLeft > 0) continue;
            ReturnStolen(loot, c);
            Log(_ctx.Strings.Format("iso_steal_over",
                ("card", loot.CardName), ("owner", loot.From?.Name ?? "?")));
        }
        if (c.Burns.Count == 0) return true;
        var report = new StringBuilder();
        ApplyHit(c, c.Burns.Count * Data.Effects.BurnDamagePerStack, "Fire", report);

        int before = c.Burns.Count;
        for (int i = 0; i < c.Burns.Count; i++) c.Burns[i]--;
        c.Burns.RemoveAll(turns => turns <= 0);
        if (c.Burns.Count < before)
            report.AppendLine(_ctx.Strings.Format("iso_burn_out",
                ("name", c.Name), ("gone", (before - c.Burns.Count).ToString()),
                ("left", c.Burns.Count.ToString())));

        Log(report.ToString().TrimEnd());
        return c.Alive;
    }

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
        var players = LivingParty;
        if (players.Count == 0) { _ctx.SwitchTo(new DeathScreen(_ctx)); return; }

        // an enemy is bound by action points exactly as the party is
        var hand = HandOf(me)
            .Where(c => !c.TargetsAllies && c.ActionCost <= me.ActionPoints).ToList();
        var reach = Pathfinder.Reachable(_level, Tile(me), me.MovePoints, _revealed,
            OccupiedExcept(me)).Cost;
        var stands = reach.Keys.Append(Tile(me)).ToList();

        // longest reach first within each kind, so a spear beats a fist
        foreach (var card in hand.Where(c => c.Delivery == Delivery.Melee)
                                 .OrderByDescending(c => c.Range)
                                 .Concat(hand.Where(c => c.Delivery != Delivery.Melee)
                                             .OrderByDescending(c => c.Range)))
        {
            // the cheapest square that puts somebody in this card's range
            var shot = players
                .SelectMany(p => stands.Select(sq => (Square: sq, Target: p)))
                .Where(x => IsoMath.GridDistance(x.Square, Tile(x.Target)) <= card.Range)
                .OrderBy(x => reach.TryGetValue(x.Square, out int c) ? c : 0)
                .ThenBy(x => IsoMath.GridDistance(x.Square, Tile(x.Target)))
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
            var near = players.OrderBy(p => IsoMath.GridDistance(Tile(me), Tile(p))).First();
            int wanted = hand.Max(c => c.Range);
            var goal = Pathfinder.StepToward(_level, Tile(me), Tile(near), me.MovePoints,
                wanted, _revealed, OccupiedExcept(me), out var path);
            me.MovePoints = 0;
            if (goal != null && path.Count > 0)
            {
                _walker = me;
                _walkFrom = Tile(me);
                _walkPath = path;
                _walkT = 0f;
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
        if (!victim.Alive || IsoMath.GridDistance(Tile(me), Tile(victim)) > card.Range)
        {
            NextTurn();
            return;
        }
        _selectedCard = card;
        PlayCard(new List<CharacterInstance> { victim }, Tile(victim));
    }

    // ---------------- drawing ----------------

    private Point? FindTileAt(Vector2 screen)
    {
        foreach (var b in _level.Blocks.Values
                     .Where(b => _revealed.Contains(b.Room))
                     .OrderByDescending(b => b.X + b.Y))
            if (IsoMath.HitsTop(screen, b.X, b.Y, b.Height, Origin))
                return new Point(b.X, b.Y);
        return null;
    }

    private Rectangle SpriteRect(CharacterInstance c)
    {
        var tex = _ctx.Assets.LoadTexture(c.SpritePath);
        float scale = _ctx.Config.CastScale(c.Name);
        int h = (int)(460 * scale);
        int w = (int)(h * tex.Width / (float)tex.Height);
        var foot = FootOf(c);
        return new Rectangle((int)(foot.X - w / 2f), (int)(foot.Y - h), w, h);
    }

    public void Draw(SpriteBatch batch)
    {
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height), Color.Black);

        var byTile = new Dictionary<Point, List<CharacterInstance>>();
        foreach (var c in Everyone.Where(c => c.Alive))
        {
            var key = c == _walker && _walkPath.Count > 0 ? _walkPath[0] : Tile(c);
            if (!byTile.TryGetValue(key, out var list)) byTile[key] = list = new List<CharacterInstance>();
            list.Add(c);
        }

        bool armed = _cardArmed;
        // Ctrl fades everything standing on the ground so the grid reads
        // clearly, and lights the square under the cursor
        float alpha = _ctrl ? CtrlFade : 1f;
        var hovered = _ctrl ? FindTileAt(_pointer.ToVector2()) : null;

        foreach (var block in _level.Blocks.Values
                     .Where(b => _revealed.Contains(b.Room))
                     .OrderBy(b => b.X + b.Y).ThenBy(b => b.X))
        {
            DrawBlock(batch, block);
            var tile = new Point(block.X, block.Y);

            // each region gets a colour wash inside plus a border around the
            // outside; red replaces blue whenever a card is armed or hovered.
            // The wash strengths all come from Config.txt.
            if (armed)
            {
                if (_rangeSet.Contains(tile))
                    Region(batch, tile, block.Height, _rangeSet, new Color(255, 70, 70),
                        _rangeOpacityKey, 7f);
            }
            else if (_moveSet.ContainsKey(tile))
                Region(batch, tile, block.Height, _moveSet.Keys, new Color(90, 150, 255),
                    "Movement", 7f);
            if (_blastSet.Contains(tile))
                Region(batch, tile, block.Height, _blastSet, new Color(190, 100, 255),
                    _blastOpacityKey, 9f);
            if (_level.TriggerAt(tile) is { Fired: false })
            {
                Fill(batch, tile, block.Height, Color.Violet * _ctx.Config.Opacity("Trigger"));
                Edge(batch, tile, block.Height, Color.Violet * 0.8f);
            }

            if (hovered == tile)
            {
                Fill(batch, tile, block.Height, Color.Yellow * _ctx.Config.Opacity("Hover"));
                Edge(batch, tile, block.Height, Color.Yellow);
            }

            if (_level.DoorAt(tile) is LevelDoor door)
                Billboard(batch, "Content/Images/Decorations/Door.png", tile, block.Height,
                    (door.Open ? Color.White * 0.35f : Color.White) * alpha);
            if (_level.DecorationAt(tile) is LevelDecoration deco)
                Billboard(batch, BlockCatalog.DecorationPath(deco.File), tile, block.Height,
                    Color.White * alpha);

            if (byTile.TryGetValue(tile, out var standing))
                foreach (var c in standing)
                    DrawCharacter(batch, c, alpha);
        }

        DrawProjectile(batch);
        DrawHud(batch);
        DrawLog(batch);
        if (DialogueActive) DrawDialogue(batch);
        _tap = null;
    }

    private void DrawBlock(SpriteBatch batch, LevelBlock block)
    {
        var top = IsoMath.ToScreen(block.X, block.Y, block.Height, Origin);
        var side = _ctx.Assets.LoadTexture(BlockCatalog.SidePath(block.Type));
        for (int f = 0; f < block.Height; f++)
            batch.Draw(side, new Rectangle((int)(top.X - IsoMath.TileW / 2f),
                (int)(top.Y + f * IsoMath.FootPx), IsoMath.TileW, IsoMath.FootPx), Color.White);
        if (block.Height == 0)
            batch.Draw(side, new Rectangle((int)(top.X - IsoMath.TileW / 2f),
                (int)top.Y, IsoMath.TileW, IsoMath.FootPx / 2), Color.White * 0.8f);
        batch.Draw(_ctx.Assets.LoadTexture(BlockCatalog.TopPath(block.Type)),
            new Rectangle((int)(top.X - IsoMath.TileW / 2f), (int)(top.Y - IsoMath.TileH / 2f),
                IsoMath.TileW, IsoMath.TileH), Color.White);
    }

    private Rectangle DiamondRect(Point tile, int height)
    {
        var c = IsoMath.ToScreen(tile.X, tile.Y, height, Origin);
        return new Rectangle((int)(c.X - IsoMath.TileW / 2f), (int)(c.Y - IsoMath.TileH / 2f),
            IsoMath.TileW, IsoMath.TileH);
    }

    private void Fill(SpriteBatch batch, Point tile, int height, Color color)
    {
        if (color.A == 0) return;
        batch.Draw(_ctx.Assets.LoadTexture("Content/Images/Blocks/OverlayTop.png"),
            DiamondRect(tile, height), color);
    }

    /// <summary>
    /// One tile of a highlighted region: a colour wash across its top face at
    /// the strength Config.txt asks for, plus the region's border where this
    /// tile faces out of it. Painting every tile this way leaves a solid area
    /// with a clean edge and no grid lines through the middle.
    /// </summary>
    private void Region(SpriteBatch batch, Point tile, int height, ICollection<Point> region,
        Color color, string opacityKey, float thickness)
    {
        Fill(batch, tile, height, color * _ctx.Config.Opacity(opacityKey));
        Outline(batch, tile, height, region, color, thickness);
    }

    /// <summary>
    /// Draws only the sides of this tile's diamond that face OUT of the region —
    /// do it for every tile in a region and what's left is its border alone,
    /// with none of the inner grid running through it.
    /// </summary>
    private void Outline(SpriteBatch batch, Point tile, int height,
        ICollection<Point> region, Color color, float thickness)
    {
        var c = IsoMath.ToScreen(tile.X, tile.Y, height, Origin);
        var top = c + new Vector2(0, -IsoMath.TileH / 2f);
        var right = c + new Vector2(IsoMath.TileW / 2f, 0);
        var bottom = c + new Vector2(0, IsoMath.TileH / 2f);
        var left = c + new Vector2(-IsoMath.TileW / 2f, 0);

        // +X lies down-right on screen, +Y down-left
        if (!region.Contains(new Point(tile.X + 1, tile.Y)))
            Ui.Line(batch, _ctx.Pixel, right, bottom, thickness, color);
        if (!region.Contains(new Point(tile.X - 1, tile.Y)))
            Ui.Line(batch, _ctx.Pixel, left, top, thickness, color);
        if (!region.Contains(new Point(tile.X, tile.Y + 1)))
            Ui.Line(batch, _ctx.Pixel, bottom, left, thickness, color);
        if (!region.Contains(new Point(tile.X, tile.Y - 1)))
            Ui.Line(batch, _ctx.Pixel, top, right, thickness, color);
    }

    /// <summary>Grid lines: the diamond's outline, so the highlight reads as a mesh.</summary>
    private void Edge(SpriteBatch batch, Point tile, int height, Color color) =>
        batch.Draw(_ctx.Assets.LoadTexture("Content/Images/Blocks/OverlayEdge.png"),
            DiamondRect(tile, height), color);

    private void Billboard(SpriteBatch batch, string path, Point tile, int height, Color tint)
    {
        var tex = _ctx.Assets.LoadTexture(path);
        var c = IsoMath.ToScreen(tile.X, tile.Y, height, Origin);
        int w = Math.Min(tex.Width, 420);
        int h = (int)(w * tex.Height / (float)tex.Width);
        batch.Draw(tex, new Rectangle((int)(c.X - w / 2f), (int)(c.Y + 30 - h), w, h), tint);
    }

    /// <param name="alpha">
    /// Faded right down while Ctrl is held, so the ground under everyone can be
    /// seen and aimed at. The health bar above the head keeps full strength —
    /// it is the information you are targeting with, not something in the way.
    /// </param>
    private void DrawCharacter(SpriteBatch batch, CharacterInstance c, float alpha = 1f)
    {
        var rect = SpriteRect(c);

        // While a cast is running its sheet stands in for the sprite, scaled to
        // the same height and centred on the same point, so nothing jumps when
        // it starts or stops. Everything below still hangs off the sprite's own
        // rect, so a wide frame overflows sideways without dragging the health
        // bar out with it.
        if (c.CastAnim is SpriteAnimation anim)
            batch.Draw(anim.Sheet, anim.RectFor(rect),
                anim.SourceRect(anim.FrameAt(c.CastAnimTime)), Color.White * alpha);
        else
            batch.Draw(_ctx.Assets.LoadTexture(c.SpritePath), rect, Color.White * alpha);

        if (_targets.Contains(c))
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(rect.X, rect.Y - 74, rect.Width, 12), Color.OrangeRed);

        // health bar above the head. Armor extends the bar in metallic grey, so
        // 10 health plus 5 armor makes the grey a third of its width.
        int barW = Math.Min(190, Math.Max(140, rect.Width));
        int barH = 34;
        var back = new Rectangle(rect.X + (rect.Width - barW) / 2, rect.Y - barH - 14, barW, barH);
        Ui.FillRect(batch, _ctx.Pixel, back, Color.Black * 0.72f);

        int span = Math.Max(1, c.MaxHp + c.Armor);
        int hpW = (int)(barW * Math.Clamp(c.Hp / (float)span, 0f, 1f));
        int armW = (int)(barW * Math.Clamp(c.Armor / (float)span, 0f, 1f));
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(back.X, back.Y, hpW, barH),
            c.IsPlayer ? new Color(70, 190, 70) : new Color(200, 60, 60));
        if (armW > 0)
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(back.X + hpW, back.Y, armW, barH),
                new Color(168, 172, 180));
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(back.X, back.Y, barW, 3), Color.Black * 0.5f);
        Ui.DrawTextCentered(batch, _ctx.Font,
            c.Armor > 0 ? $"{c.Hp}+{c.Armor}" : c.Hp.ToString(), back, Color.White, 0.26f);

        // whoever is selected wears a small arrow pointing down at their bar
        if (IsSelected(c)) DrawSelectionArrow(batch, back);

        if (c.CurseBonus > 0)
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(back.X, back.Bottom + 3, barW, 6), new Color(150, 60, 200));

        // burning: one flame per stack, sitting on the bar
        if (c.BurningStacks > 0)
        {
            var flame = _ctx.Assets.LoadTexture("Content/Images/Effects/Flame.png");
            int fw = barH, gap = fw - 8;
            for (int i = 0; i < Math.Min(c.BurningStacks, 4); i++)
                batch.Draw(flame, new Rectangle(back.X - fw + 4 + i * gap, back.Y - 12, fw, fw), Color.White);
            if (c.BurningStacks > 4)
                batch.DrawString(_ctx.Font, $"x{c.BurningStacks}",
                    new Vector2(back.Right + 8, back.Y), Color.Orange,
                    0f, Vector2.Zero, 0.24f, SpriteEffects.None, 0f);
        }
    }

    /// <summary>Who the blue movement region belongs to right now.</summary>
    private bool IsSelected(CharacterInstance c) => _mode is Mode.Explore or Mode.FreeMove
        ? c == _selected
        : c == Current && c.IsPlayer;

    /// <summary>A small solid triangle pointing down, sitting on top of the health bar.</summary>
    private void DrawSelectionArrow(SpriteBatch batch, Rectangle bar)
    {
        const int width = 46, height = 28;
        int baseY = bar.Y - 10 - height;
        for (int row = 0; row < height; row++)
        {
            int w = Math.Max(2, width - row * width / height);
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(bar.Center.X - w / 2, baseY + row, w, 1), Color.Gold);
        }
    }

    private void DrawProjectile(SpriteBatch batch)
    {
        if (_mode != Mode.Acting || _act != Act.Projectile || _actingCard == null) return;
        float t = _actDur <= 0f ? 1f : MathHelper.Clamp(_actT / _actDur, 0f, 1f);
        var tex = _ctx.Assets.LoadTexture($"Content/Images/Effects/{_actingCard.ProjectileArt}");
        var size = AssetLoader.DisplaySize(tex, AssetKind.Effect);
        var pos = Vector2.Lerp(_projFrom, _projTo, t);
        batch.Draw(tex, pos, null, Color.White, _projRotation,
            new Vector2(tex.Width / 2f, tex.Height / 2f),
            new Vector2(size.X / tex.Width, size.Y / tex.Height), SpriteEffects.None, 0f);
    }

    private List<Rectangle> HandRects()
    {
        int total = _hand.Count * (CardW + CardGap) - CardGap;
        int x0 = (VirtualViewport.Width - total) / 2;
        var rects = new List<Rectangle>();
        for (int i = 0; i < _hand.Count; i++)
            rects.Add(new Rectangle(x0 + i * (CardW + CardGap), CardRestY, CardW, CardH));
        return rects;
    }

    private void DrawDialogue(SpriteBatch batch)
    {
        var line = _lines![Math.Min(_lineIndex, _lines.Count - 1)];
        Ui.FillRect(batch, _ctx.Pixel, DialogueBox, new Color(0, 0, 0, 225));

        var speaker = Everyone.FirstOrDefault(c =>
            c.Name.Equals(line.Speaker, StringComparison.OrdinalIgnoreCase));
        var thumbRect = new Rectangle(DialogueBox.X + 36, DialogueBox.Y + 34, 350, 350);
        if (speaker != null)
        {
            var thumb = _ctx.Assets.LoadFirstAvailable(speaker.ThumbPath, speaker.SpritePath);
            batch.Draw(thumb, Ui.FitCentered(AssetLoader.DisplaySize(thumb, AssetKind.Thumb), thumbRect),
                Color.White);
        }
        batch.DrawString(_ctx.Font, line.Speaker,
            new Vector2(DialogueBox.X + 430, DialogueBox.Y + 36), Color.Gold,
            0f, Vector2.Zero, 0.5f, SpriteEffects.None, 0f);
        batch.DrawString(_ctx.Font, Ui.Wrap(_ctx.Font, line.Text, DialogueBox.Width - 520, 0.46f),
            new Vector2(DialogueBox.X + 430, DialogueBox.Y + 140), Color.White,
            0f, Vector2.Zero, 0.46f, SpriteEffects.None, 0f);
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("iso_dialogue_next"),
            new Rectangle(DialogueBox.X, DialogueBox.Bottom - 70, DialogueBox.Width - 40, 50),
            Color.White * 0.55f, 0.3f);
    }

    private void DrawHud(SpriteBatch batch)
    {
        if (_toastTimer > 0 && !DialogueActive)
            batch.DrawString(_ctx.Font, _toast, new Vector2(80, 260), Color.White,
                0f, Vector2.Zero, 0.42f, SpriteEffects.None, 0f);

        switch (_mode)
        {
            case Mode.Explore:
                Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("iso_explore_hint"),
                    new Rectangle(0, 40, VirtualViewport.Width, 90), Color.White * 0.7f, 0.34f);
                break;
            case Mode.FreeMove:
                Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("iso_spotted"),
                    new Rectangle(0, 40, VirtualViewport.Width, 90), Color.OrangeRed, 0.42f);
                if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, DoneRect, _ctx.Strings.Get("iso_done"), _tap))
                { _freeMovers.Clear(); StartCombat(); }
                break;
            case Mode.PlayerTurn:
            case Mode.PlayerTarget:
                DrawTurnStrip(batch);
                if (Current is CharacterInstance me)
                {
                    Ui.DrawTextCentered(batch, _ctx.Font,
                        me.MovePoints <= 0
                            ? _ctx.Strings.Get("iso_move_spent")
                            : _ctx.Strings.Format("iso_move_left", ("points", me.MovePoints.ToString())),
                        new Rectangle(0, 300, VirtualViewport.Width, 80),
                        me.MovePoints <= 0 ? Color.Gray : Color.LightBlue, 0.36f);
                    DrawActionPoints(batch, me);
                }
                if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, EndTurnRect, _ctx.Strings.Get("iso_end_turn"), _tap))
                    NextTurn();
                if (_mode == Mode.PlayerTarget)
                {
                    int wanted = _selectedCard == null ? 1 : TargetsWanted(_selectedCard);
                    string prompt = _targets.Count == 0
                        ? _ctx.Strings.Get("iso_pick_target")
                        : _ctx.Strings.Format("iso_pick_more",
                            ("count", Math.Max(1, wanted - _targets.Count).ToString()));
                    Ui.DrawTextCentered(batch, _ctx.Font, prompt,
                        new Rectangle(0, 1330, VirtualViewport.Width, 100), Color.OrangeRed, 0.44f);
                }
                DrawHand(batch);
                break;
            case Mode.EnemyTurn:
            case Mode.Acting:
                DrawTurnStrip(batch);
                break;
            case Mode.StealPick:
                DrawTurnStrip(batch);
                DrawStealPick(batch);
                break;
            case Mode.Victory:
                Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("iso_victory"),
                    new Rectangle(0, 900, VirtualViewport.Width, 250), Color.Gold, 1.0f);
                if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, WinRect, _ctx.Strings.Get("battle_win"), _tap))
                {
                    _ctx.State.CompletedMissions.Add(_level.Name);
                    _ctx.State.EndMission(completed: false);
                    _ctx.SwitchTo(new MapScreen(_ctx));
                }
                break;
        }
    }

    /// <summary>How many lines the panel shows at once.</summary>
    private static int LogLines => LogPanel.Height / LogLineH - 1;

    /// <summary>
    /// The + button, and the log panel behind it. Collapsed by default so the
    /// level is clean; open, it shows the most recent entries with the newest
    /// at the bottom, and the wheel scrolls back through the rest.
    /// </summary>
    private void DrawLog(SpriteBatch batch)
    {
        if (_logOpen)
        {
            Ui.FillRect(batch, _ctx.Pixel, LogPanel, new Color(0, 0, 0, 205));
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(LogPanel.X, LogPanel.Y, LogPanel.Width, 3), Color.White * 0.3f);

            int shown = Math.Min(LogLines, _log.Count);
            int end = _log.Count - _logScroll;          // exclusive
            int start = Math.Max(0, end - shown);
            for (int i = start; i < end; i++)
            {
                // the newest entries read brightest, older ones fade back
                float age = (end - 1 - i) / (float)Math.Max(1, shown);
                batch.DrawString(_ctx.Font, Ui.Wrap(_ctx.Font, _log[i], LogPanel.Width - 56, LogTextScale),
                    new Vector2(LogPanel.X + 28, LogPanel.Y + 26 + (i - start) * LogLineH),
                    Color.White * (1f - age * 0.45f),
                    0f, Vector2.Zero, LogTextScale, SpriteEffects.None, 0f);
            }
            if (_log.Count == 0)
                Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("iso_log_empty"),
                    LogPanel, Color.White * 0.4f, LogTextScale);
            if (_logScroll > 0)
                Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("iso_log_more"),
                    new Rectangle(LogPanel.X, LogPanel.Bottom - 46, LogPanel.Width, 40),
                    Color.Gold * 0.8f, 0.26f);
        }

        Ui.FillRect(batch, _ctx.Pixel, LogToggleRect, new Color(20, 20, 28, 235));
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(LogToggleRect.X, LogToggleRect.Y, LogToggleRect.Width, 3), Color.White * 0.5f);
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(LogToggleRect.X, LogToggleRect.Bottom - 3, LogToggleRect.Width, 3), Color.White * 0.5f);
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(LogToggleRect.X, LogToggleRect.Y, 3, LogToggleRect.Height), Color.White * 0.5f);
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(LogToggleRect.Right - 3, LogToggleRect.Y, 3, LogToggleRect.Height), Color.White * 0.5f);

        // a drawn +/- rather than a glyph, so it stays centred at any font size
        var c = LogToggleRect.Center;
        var ink = _logOpen ? Color.Gold : Color.White;
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(c.X - 26, c.Y - 4, 52, 8), ink);
        if (!_logOpen)
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(c.X - 4, c.Y - 26, 8, 52), ink);
    }

    /// <summary>
    /// Turn order as a row of faces rather than a row of names. Whoever is
    /// acting sits at the far left, so the strip shuffles along by one every
    /// turn and "next up" is always the face beside the big one.
    /// </summary>
    private void DrawTurnStrip(SpriteBatch batch)
    {
        if (_order.Count == 0) return;

        // rotate the running order so the current actor leads it, skipping
        // anyone who has died and won't be taking a turn
        var upcoming = new List<CharacterInstance>();
        for (int step = 0; step < _order.Count; step++)
        {
            var who = _order[(_turn + step + _order.Count) % _order.Count];
            if (who.Alive) upcoming.Add(who);
        }
        if (upcoming.Count == 0) return;

        int x = TurnStrip.X;
        for (int i = 0; i < upcoming.Count && x < TurnStrip.Right; i++)
        {
            var who = upcoming[i];
            bool active = i == 0;
            int size = active ? TurnFaceActive : TurnFace;
            var slot = new Rectangle(x, TurnStrip.Y + (TurnFaceActive - size) / 2, size, size);

            // the acting character gets a gold frame; the rest sit dimmer and smaller
            var frame = active ? Color.Gold : Color.White * 0.4f;
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(slot.X - 4, slot.Y - 4, slot.Width + 8, slot.Height + 8), frame);
            Ui.FillRect(batch, _ctx.Pixel, slot, new Color(16, 16, 22));

            var face = _ctx.Assets.LoadFirstAvailable(who.ThumbPath, who.SpritePath);
            var fit = Ui.FitCentered(new Vector2(face.Width, face.Height),
                new Rectangle(slot.X + 4, slot.Y + 4, slot.Width - 8, slot.Height - 8));
            batch.Draw(face, fit, active ? Color.White : Color.White * 0.65f);

            // a thin bar under each face says which side it is on
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(slot.X, slot.Bottom - 10, slot.Width, 10),
                who.IsPlayer ? new Color(70, 190, 70) : new Color(200, 60, 60));

            x += size + TurnFaceGap;
        }

        if (Current != null)
            batch.DrawString(_ctx.Font,
                _ctx.Strings.Format("battle_turn", ("name", Current.Name)),
                new Vector2(TurnStrip.X, TurnStrip.Bottom + 12), Color.Gold,
                0f, Vector2.Zero, 0.38f, SpriteEffects.None, 0f);
    }

    /// <summary>
    /// Action points as a row of pips: filled for what's left, hollow for what
    /// has been spent this turn. A third pip appears only when a point was
    /// carried over, which is the whole tell that rollover happened.
    /// </summary>
    private void DrawActionPoints(SpriteBatch batch, CharacterInstance who)
    {
        int slots = Math.Max(CharacterInstance.ActionsPerTurn, who.ActionPoints);
        const int pip = 46, gap = 16;
        int total = slots * pip + (slots - 1) * gap;
        int x = (VirtualViewport.Width - total) / 2;
        int y = 232;

        for (int i = 0; i < slots; i++)
        {
            var box = new Rectangle(x + i * (pip + gap), y, pip, pip);
            bool full = i < who.ActionPoints;
            Ui.FillRect(batch, _ctx.Pixel, box, full ? Color.Orange : new Color(60, 45, 30));
            if (full) continue;
            // hollow: draw the frame only
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(box.X + 5, box.Y + 5, pip - 10, pip - 10),
                new Color(18, 18, 24));
        }
        var label = _ctx.Strings.Format("iso_actions_left",
            ("points", who.ActionPoints.ToString()));
        batch.DrawString(_ctx.Font, label, new Vector2(x + total + 28, y + 4),
            who.ActionPoints > 0 ? Color.Orange : Color.Gray,
            0f, Vector2.Zero, 0.32f, SpriteEffects.None, 0f);
    }

    private void DrawHand(SpriteBatch batch)
    {
        var rects = HandRects();
        int hovered = -1;
        for (int i = 0; i < _hand.Count; i++)
            if (rects[i].Contains(_pointer)) hovered = i;

        for (int i = 0; i < _hand.Count; i++)
            if (i != hovered)
                DrawCard(batch, _hand[i], rects[i], false);
        if (hovered >= 0)
        {
            int w = (int)(CardW * HoverScale), h = (int)(CardH * HoverScale);
            DrawCard(batch, _hand[hovered], new Rectangle(
                rects[hovered].Center.X - w / 2, VirtualViewport.Height - h - 30, w, h), true);
        }
    }

    private void DrawCard(SpriteBatch batch, Card card, Rectangle rect, bool hovered)
    {
        float s = hovered ? HoverScale : 1f;
        // a card the holder cannot currently afford is greyed out card by card,
        // so with 1 point left the cheap ones stay lit and the dear ones dim
        bool spent = Current == null || Current.ActionPoints < card.ActionCost;
        Ui.FillRect(batch, _ctx.Pixel, rect,
            spent ? new Color(20, 20, 26, 250) : new Color(24, 24, 40, 250));
        var border = card == _selectedCard ? Color.Gold
            : spent ? Color.White * 0.25f
            : hovered ? Color.White : Color.White * 0.5f;
        int bw = (int)(6 * s);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Y, rect.Width, bw), border);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Bottom - bw, rect.Width, bw), border);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Y, bw, rect.Height), border);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.Right - bw, rect.Y, bw, rect.Height), border);

        var ink = spent ? Color.White * 0.4f : Color.White;
        Ui.DrawTextCentered(batch, _ctx.Font, card.Name,
            new Rectangle(rect.X, rect.Y + (int)(18 * s), rect.Width, (int)(80 * s)), ink, 0.4f * s);
        batch.DrawString(_ctx.Font, Ui.Wrap(_ctx.Font, card.CardText, rect.Width - 56 * s, 0.3f * s),
            new Vector2(rect.X + 28 * s, rect.Y + 120 * s), ink * 0.9f,
            0f, Vector2.Zero, 0.3f * s, SpriteEffects.None, 0f);

        int hitCount = card.TargetsGround && _blastSet.Count > 0
            ? VisibleEnemies.Count(e => _blastSet.Contains(Tile(e)))
            : VisibleEnemies.Count;
        string total = card.Damage <= 0 && card.Effects.Count > 0
            ? $"+{card.Effects[0].Amount} {card.Effects[0].Name}"
            : $"{card.TotalDamage(hitCount)} {card.DamageType}";
        var size = _ctx.Font.MeasureString(total) * (0.36f * s);
        batch.DrawString(_ctx.Font, total,
            new Vector2(rect.Right - 24 * s - size.X, rect.Bottom - 28 * s - size.Y),
            spent ? Color.Gold * 0.4f : Color.Gold, 0f, Vector2.Zero, 0.36f * s, SpriteEffects.None, 0f);
        batch.DrawString(_ctx.Font, _ctx.Strings.Format("iso_card_range", ("range", card.Range.ToString())),
            new Vector2(rect.X + 24 * s, rect.Bottom - 28 * s - size.Y),
            spent ? Color.LightBlue * 0.4f : Color.LightBlue,
            0f, Vector2.Zero, 0.3f * s, SpriteEffects.None, 0f);

        // the cost, top-right, as one pip per point
        for (int i = 0; i < card.ActionCost; i++)
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle((int)(rect.Right - (28 + i * 34) * s), (int)(rect.Y + 22 * s),
                    (int)(24 * s), (int)(24 * s)),
                spent ? Color.Orange * 0.4f : Color.Orange);
    }
}
