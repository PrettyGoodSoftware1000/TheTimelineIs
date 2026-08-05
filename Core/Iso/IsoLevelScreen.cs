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
/// the other two characters first get a free positioning move, then turn
/// order rolls (sides shuffled, alternating).
///
/// A turn is movement THEN a card: reachable tiles show as blue fill with blue
/// grid lines, and playing a card ends movement for that turn. Hovering or
/// selecting a card paints how far it reaches in red beyond the blue.
///
/// Targeting always takes two clicks. The first click on an enemy arms it and
/// lights every legal approach square in yellow — for melee that is the north,
/// east, south and west neighbours it can afford — with the one the cursor
/// points at picked out brighter; a second click on an armed target walks
/// there and strikes. A card wanting several targets collects one per click
/// and fires once it has them all. A blast card also paints its explosion
/// radius in purple, following the cursor until a target is locked in, and
/// damages whatever that purple covers rather than everything in throwing
/// range. Right-click cancels.
///
/// Stepping on a trigger square plays its dialogue block once.
/// </summary>
public class IsoLevelScreen : IScreen
{
    private enum Mode { Explore, FreeMove, PlayerTurn, PlayerTarget, EnemyTurn, Acting, Victory }
    private enum Act { Casting, Projectile, MeleeWait, Hits, EnemyWindup }

    private const int AggroTiles = 15;
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
    private bool _playedCard;
    private List<Card> _hand = new();
    private Card? _selectedCard;
    private readonly List<CharacterInstance> _targets = new();  // armed targets, in click order
    private Point? _approach;                    // the yellow square we'd actually use
    private List<Point> _approachOptions = new();// every legal approach square, also yellow
    private Point? _blastCenter;                 // blast or cone aim point
    private Point? _armedTile;                   // ground card: the tile locked in by the first click
    private HashSet<Point> _blastSet = new();    // purple: tiles the area would cover

    // overlays, recomputed only when the mover, position, or card changes
    private Dictionary<Point, int> _moveSet = new();
    private HashSet<Point> _rangeSet = new();
    private object? _overlayKey;

    private Vector2 _camera;
    private Vector2 _baseOrigin;
    private Point _pointer;
    private Point? _tap;
    private string _toast = "";
    private float _toastTimer;

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
        _selected = _party.FirstOrDefault();
        Toast(_ctx.Strings.Get("iso_enter"));
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
                SpriteFile = PickSprite(cls?.SpriteFiles, names[i], _party),
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
                AttackDmg = def.AttackDamage,
                AttackSound = def.AttackSound,
                RangeTiles = def.Range,
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

    private CharacterInstance? Current => _turn >= 0 && _turn < _order.Count ? _order[_turn] : null;
    private bool DialogueActive => _lines != null;

    private void Toast(string text) { _toast = text; _toastTimer = 3f; }

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
        return at + new Vector2(0, 26) + Formation.ShakeOffset(c);
    }

    // ---------------- update ----------------

    public void Update(InputState input, float dt)
    {
        _pointer = input.PointerPos;
        _tap = input.Tap;
        _camera += input.PanDelta;
        if (_toastTimer > 0) _toastTimer -= dt;
        Formation.UpdateShakes(_party.Concat(_enemies).ToList(), dt);

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
            case Mode.Acting: UpdateAction(dt); break;
            case Mode.EnemyTurn: EnemyAct(); break;
            case Mode.Explore:
            case Mode.FreeMove:
            case Mode.PlayerTurn:
            case Mode.PlayerTarget:
                UpdateApproach();
                HandleClicks();
                break;
        }
    }

    private void CancelCard()
    {
        if (_selectedCard == null && _targets.Count == 0 && _armedTile == null) return;
        _selectedCard = null;
        _targets.Clear();
        _approach = null;
        _approachOptions.Clear();
        _blastCenter = null;
        _armedTile = null;
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
            Toast(_ctx.Strings.Get("iso_spotted"));
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
            Toast(_ctx.Strings.Get("iso_clear"));
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
        _overlayKey = null;
        if (!BurnAtTurnStart(current)) { NextTurn(); return; }
        if (current.IsPlayer)
        {
            _playedCard = false;
            _hand = _ctx.Cards.HandFor(_ctx.Classes.CardTagsFor(current.Name));
            _mode = Mode.PlayerTurn;
        }
        else
        {
            _mode = Mode.EnemyTurn;
        }
    }

    // ---------------- overlays ----------------

    /// <summary>Blue = where the mover can walk; red = where the armed card could reach from there.</summary>
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
        if (mover == null) return;

        int budget = _mode == Mode.Explore ? 9999 : mover.MovePoints;
        _moveSet = Pathfinder.Reachable(_level, Tile(mover), budget, _revealed, OccupiedExcept(mover)).Cost;
        if (card == null || _mode == Mode.Explore) return;

        // every tile the card could strike from anywhere the mover can stand
        var stands = _moveSet.Keys.Append(Tile(mover)).ToList();
        foreach (var block in _level.Blocks.Values)
        {
            if (!_revealed.Contains(block.Room)) continue;
            var tile = new Point(block.X, block.Y);
            if (_moveSet.ContainsKey(tile)) continue;      // blue already covers it
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
    /// Recomputes the yellow approach squares and, for a blast card, the purple
    /// area it would cover. Every legal approach square is shown; the one the
    /// cursor points at is the one that will be used, so a melee attacker can
    /// be staged north, east, south or west of its target.
    /// </summary>
    private void UpdateApproach()
    {
        _approach = null;
        _approachOptions = new List<Point>();
        _blastCenter = null;
        _blastSet = new HashSet<Point>();
        if (_selectedCard == null || Current == null) return;
        var card = _selectedCard;

        if (_targets.Count > 0)
        {
            _approachOptions = ApproachOptions(Current, _targets, card);
            _approach = PickApproach(_approachOptions, Tile(_targets[0]), card);
        }

        if (card.TargetsGround)
        {
            // purple follows the cursor until a tile is locked in
            var center = _armedTile
                ?? (_targets.Count > 0 ? Tile(_targets[0]) : FindTileAt(_pointer.ToVector2()));
            if (center is Point c && ReachableAim(Current, c, card))
            {
                _blastCenter = c;
                _blastSet = AreaOf(card, Tile(Current), c);
            }
        }
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

    /// <summary>Could the card be thrown at this tile from anywhere the caster can stand?</summary>
    private bool ReachableAim(CharacterInstance me, Point aim, Card card) =>
        _moveSet.Keys.Append(Tile(me)).Any(s => IsoMath.GridDistance(s, aim) <= card.Range);

    /// <summary>Squares the caster could act from with every chosen target in range.</summary>
    private List<Point> ApproachOptions(CharacterInstance me, List<CharacterInstance> targets, Card card)
    {
        var here = Tile(me);
        if (card.Delivery == Delivery.Cone) return new List<Point> { here };
        bool InRange(Point from) => targets.All(t => IsoMath.GridDistance(from, Tile(t)) <= card.Range);
        if (InRange(here)) return new List<Point> { here };
        return _moveSet.Keys.Where(InRange).ToList();
    }

    /// <summary>
    /// Melee takes the option the cursor points at, relative to the target;
    /// ranged just moves as little as it can.
    /// </summary>
    private Point? PickApproach(List<Point> options, Point focus, Card card)
    {
        if (options.Count == 0) return null;
        int Cost(Point t) => _moveSet.TryGetValue(t, out int c) ? c : 0;
        if (card.Delivery != Delivery.Melee)
            return options.OrderBy(Cost).First();

        var center = IsoMath.ToScreen(focus.X, focus.Y, HeightAt(focus), Origin);
        var want = _pointer.ToVector2() - center;
        if (want.LengthSquared() < 1f) return options.OrderBy(Cost).First();
        want.Normalize();

        return options.OrderByDescending(t =>
        {
            var dir = IsoMath.ToScreen(t.X, t.Y, HeightAt(t), Origin) - center;
            if (dir.LengthSquared() < 1f) return -1f;
            dir.Normalize();
            return Vector2.Dot(dir, want);
        }).ThenBy(Cost).First();
    }

    // ---------------- input ----------------

    private void HandleClicks()
    {
        if (_tap is not Point press) return;
        _tap = null;

        if (_mode is Mode.PlayerTurn or Mode.PlayerTarget && HandleCardClick(press)) return;
        if (HitButton(press)) return;

        foreach (var c in Everyone.Reverse())
        {
            if (!c.Alive || !SpriteRect(c).Contains(press)) continue;
            if (_mode == Mode.PlayerTarget && _selectedCard is Card aiming)
            {
                if (aiming.TargetsGround) TryTargetGround(Tile(c));
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

    private void OpenDoor(LevelDoor door)
    {
        door.Open = true;
        _revealed.Add(door.RoomA);
        _revealed.Add(door.RoomB);
        _overlayKey = null;
        Toast(_ctx.Strings.Get("iso_door_open"));
        var nearest = LivingParty.OrderBy(p =>
            IsoMath.GridDistance(Tile(p), new Point(door.X, door.Y))).First();
        CheckAggro(nearest);
    }

    private void HandleTileClick(Point tile)
    {
        var mover = ActiveMover;
        if (mover == null || !mover.Alive) return;
        if (_mode is Mode.PlayerTurn or Mode.PlayerTarget && _playedCard)
        {
            Toast(_ctx.Strings.Get("iso_move_spent"));
            return;
        }
        if (!_moveSet.TryGetValue(tile, out int spent)) return;

        if (_mode != Mode.Explore) mover.MovePoints -= spent;
        BeginWalk(mover, tile, null);
    }

    private void BeginWalk(CharacterInstance mover, Point goal, Action? after)
    {
        int budget = _mode == Mode.Explore ? 9999 : mover.MoveMax;
        var (_, parent) = Pathfinder.Reachable(_level, Tile(mover), budget, _revealed, OccupiedExcept(mover));
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
                if (_playedCard) { Toast(_ctx.Strings.Get("iso_card_spent")); return true; }
                _selectedCard = _hand[i];
                _targets.Clear();
                _approach = null;
                _mode = Mode.PlayerTarget;
                _overlayKey = null;
                return true;
            }
        return false;
    }

    /// <summary>
    /// Two clicks, always. The first click on an enemy arms it — showing the
    /// yellow approach squares and, for a blast card, the purple area — and a
    /// second click on an armed target commits. A card wanting several targets
    /// collects one per click and fires once it has them all.
    /// </summary>
    private void TryTarget(CharacterInstance enemy)
    {
        var card = _selectedCard!;
        var me = Current!;
        int wanted = TargetsWanted(card);

        if (_targets.Contains(enemy))
        {
            // clicking an armed target again is the confirm
            if (_targets.Count >= wanted) Commit(me, card);
            else Toast(_ctx.Strings.Format("iso_pick_more",
                ("count", (wanted - _targets.Count).ToString())));
            return;
        }

        if (_targets.Count >= wanted)
        {
            // all slots full and this is somebody new: re-aim at them instead
            _targets.Clear();
        }
        _targets.Add(enemy);

        var options = ApproachOptions(me, _targets, card);
        if (options.Count == 0)
        {
            _targets.Remove(enemy);
            Toast(_ctx.Strings.Get("iso_out_of_range"));
            return;
        }
        if (_targets.Count < wanted)
            Toast(_ctx.Strings.Format("iso_pick_more", ("count", (wanted - _targets.Count).ToString())));
    }

    /// <summary>
    /// Ground aiming, for blasts and cones: the first click locks the tile in,
    /// a second click on the same tile fires. Anything the purple covers is hit.
    /// </summary>
    private void TryTargetGround(Point tile)
    {
        var card = _selectedCard!;
        var me = Current!;
        if (!ReachableAim(me, tile, card)) { Toast(_ctx.Strings.Get("iso_out_of_range")); return; }

        if (_armedTile != tile)
        {
            _armedTile = tile;
            _targets.Clear();
            _overlayKey = null;
            return;
        }

        var options = ApproachOptions(me, new List<CharacterInstance>(), card);
        var square = card.Delivery == Delivery.Cone
            ? Tile(me)
            : _moveSet.Keys.Append(Tile(me))
                .Where(s2 => IsoMath.GridDistance(s2, tile) <= card.Range)
                .OrderBy(s2 => _moveSet.TryGetValue(s2, out int c) ? c : 0).FirstOrDefault(Tile(me));

        var area = AreaOf(card, square, tile);
        if (square == Tile(me)) { PlayArea(area, tile); return; }
        me.MovePoints -= _moveSet.TryGetValue(square, out int cost) ? cost : 0;
        var aim = tile;
        BeginWalk(me, square, () => PlayArea(AreaOf(card, Tile(me), aim), aim));
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
        var square = PickApproach(ApproachOptions(me, _targets, card), Tile(_targets[0]), card);
        if (square == null) { Toast(_ctx.Strings.Get("iso_out_of_range")); return; }

        var aimTile = Tile(_targets[0]);
        var shots = _targets.ToList();
        if (square.Value == Tile(me))
        {
            PlayCard(shots, aimTile);
            return;
        }
        me.MovePoints -= _moveSet.TryGetValue(square.Value, out int c) ? c : 0;
        BeginWalk(me, square.Value, () => PlayCard(shots, aimTile));
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
        _approach = null;
        _approachOptions.Clear();
        _blastCenter = null;
        _armedTile = null;
        _blastSet.Clear();
        _playedCard = true;
        _actor!.MovePoints = 0;      // a card ends this turn's movement, unless Nimble gives it back
        _overlayKey = null;

        _ctx.Sounds.Play(card.CastingSound);
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
                if (aim == null) { FinishAction(); return; }
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
            case Act.EnemyWindup:
                ResolveEnemyHit();
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
            ApplyHit(v, dmg, card.DamageType, report);

        _hitIndex++;
        bool lastBlow = _hitIndex >= card.HitEvents.Count;
        if (lastBlow && card.Effects.Count > 0)
            ApplyEffects(card, struck, report);
        if (report.Length > 0) Toast(report.ToString().TrimEnd());

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
        _actor = null;
        _victims.Clear();
        _overlayKey = null;
        if (LivingParty.Count == 0) { _ctx.SwitchTo(new DeathScreen(_ctx)); return; }
        // Nimble hands movement back, so the turn continues instead of ending
        var mover = Current;
        if (mover != null && mover.IsPlayer && mover.Alive && mover.MovePoints > 0)
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
        target.ShakeTimer = Formation.ShakeDuration;

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
            if (effect.Is(Data.Effects.Nimble))
            {
                // Nimble hands movement back to the caster, not to the victims
                if (_actor != null)
                {
                    _actor.MovePoints += effect.Amount;
                    report.AppendLine(_ctx.Strings.Format("iso_nimble",
                        ("name", _actor.Name), ("points", effect.Amount.ToString())));
                }
                continue;
            }
            foreach (var c in hit.Where(c => c.Alive))
            {
                if (effect.Is(Data.Effects.Burning))
                {
                    c.BurningStacks += effect.Amount;
                    c.BurningTurns = Data.Effects.BurnTurns;   // a fresh stack refreshes the timer
                    report.AppendLine(_ctx.Strings.Format("iso_burning",
                        ("name", c.Name), ("stacks", c.BurningStacks.ToString())));
                }
                else if (effect.Is(Data.Effects.Armor))
                {
                    c.Armor += effect.Amount;
                    report.AppendLine(_ctx.Strings.Format("iso_armored",
                        ("name", c.Name), ("armor", c.Armor.ToString())));
                }
            }
        }
    }

    /// <summary>Burning bites at the victim's own turn start. Returns false if it killed them.</summary>
    private bool BurnAtTurnStart(CharacterInstance c)
    {
        if (c.BurningStacks <= 0) return true;
        var report = new StringBuilder();
        ApplyHit(c, c.BurningStacks * Data.Effects.BurnDamagePerStack, "Fire", report);
        c.BurningTurns--;
        if (c.BurningTurns <= 0) { c.BurningStacks = 0; report.AppendLine(
            _ctx.Strings.Format("iso_burn_out", ("name", c.Name))); }
        Toast(report.ToString().TrimEnd());
        return c.Alive;
    }

    private void EnemyAct()
    {
        var me = Current!;
        var players = LivingParty;
        if (players.Count == 0) { _ctx.SwitchTo(new DeathScreen(_ctx)); return; }
        var target = players.OrderBy(p => IsoMath.GridDistance(Tile(me), Tile(p))).First();

        if (IsoMath.GridDistance(Tile(me), Tile(target)) > me.RangeTiles)
        {
            var goal = Pathfinder.StepToward(_level, Tile(me), Tile(target), me.MovePoints,
                me.RangeTiles, _revealed, OccupiedExcept(me), out var path);
            if (goal != null && path.Count > 0)
            {
                _walker = me;
                _walkFrom = Tile(me);
                _walkPath = path;
                _walkT = 0f;
                _afterWalk = () => EnemyStrikeOrPass(me, target);
                return;
            }
        }
        EnemyStrikeOrPass(me, target);
    }

    private void EnemyStrikeOrPass(CharacterInstance me, CharacterInstance target)
    {
        if (target.Alive && IsoMath.GridDistance(Tile(me), Tile(target)) <= me.RangeTiles)
        {
            _actor = me;
            _victims = new List<CharacterInstance> { target };
            _mode = Mode.Acting;
            EnterAct(Act.EnemyWindup, 0.35f);
        }
        else NextTurn();
    }

    private void ResolveEnemyHit()
    {
        var me = _actor!;
        var target = _victims.FirstOrDefault();
        if (target != null && target.Alive)
        {
            _ctx.Sounds.Play(me.AttackSound);
            var report = new StringBuilder(_ctx.Strings.Format("battle_enemy_hit",
                ("attacker", me.Name), ("target", target.Name),
                ("dmg", me.AttackDmg.ToString()), ("type", me.AttackType)));
            target.Hp -= me.AttackDmg;
            target.ShakeTimer = Formation.ShakeDuration;
            if (target.Hp <= 0)
            {
                target.Hp = 0;
                target.Alive = false;
                report.Append('\n').Append(_ctx.Strings.Format("battle_down", ("name", target.Name)));
            }
            Toast(report.ToString());
        }
        _actor = null;
        _victims.Clear();
        NextTurn();
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

        foreach (var block in _level.Blocks.Values
                     .Where(b => _revealed.Contains(b.Room))
                     .OrderBy(b => b.X + b.Y).ThenBy(b => b.X))
        {
            DrawBlock(batch, block);
            var tile = new Point(block.X, block.Y);

            if (_rangeSet.Contains(tile))
            {
                Fill(batch, tile, block.Height, new Color(255, 60, 60) * 0.18f);
                Edge(batch, tile, block.Height, new Color(255, 70, 70) * 0.75f);
            }
            if (_moveSet.ContainsKey(tile))
            {
                Fill(batch, tile, block.Height, Color.Blue * 0.2f);
                Edge(batch, tile, block.Height, new Color(90, 150, 255) * 0.9f);
            }
            if (_blastSet.Contains(tile))
            {
                Fill(batch, tile, block.Height, new Color(170, 70, 255) * 0.3f);
                Edge(batch, tile, block.Height, new Color(190, 100, 255) * 0.9f);
            }
            if (_approachOptions.Contains(tile))
            {
                bool chosen = _approach == tile;
                Fill(batch, tile, block.Height, Color.Yellow * (chosen ? 0.4f : 0.15f));
                Edge(batch, tile, block.Height, Color.Yellow * (chosen ? 1f : 0.45f));
            }
            if (_level.TriggerAt(tile) is { Fired: false })
                Edge(batch, tile, block.Height, Color.Violet * 0.8f);

            if (_level.DoorAt(tile) is LevelDoor door)
                Billboard(batch, "Content/Images/Decorations/Door.png", tile, block.Height,
                    door.Open ? Color.White * 0.35f : Color.White);
            if (_level.DecorationAt(tile) is LevelDecoration deco)
                Billboard(batch, BlockCatalog.DecorationPath(deco.File), tile, block.Height, Color.White);

            if (byTile.TryGetValue(tile, out var standing))
                foreach (var c in standing)
                    DrawCharacter(batch, c);
        }

        DrawProjectile(batch);
        DrawHud(batch);
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

    private void Fill(SpriteBatch batch, Point tile, int height, Color color) =>
        batch.Draw(_ctx.Assets.LoadTexture("Content/Images/Blocks/OverlayTop.png"),
            DiamondRect(tile, height), color);

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

    private void DrawCharacter(SpriteBatch batch, CharacterInstance c)
    {
        var rect = SpriteRect(c);
        batch.Draw(_ctx.Assets.LoadTexture(c.SpritePath), rect, Color.White);

        if (c == _selected && _mode is Mode.Explore or Mode.FreeMove)
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Bottom + 6, rect.Width, 8), Color.White);
        if (c == Current && _mode is not (Mode.Explore or Mode.FreeMove))
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Bottom + 6, rect.Width, 8), Color.Gold);
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
                    Ui.DrawTextCentered(batch, _ctx.Font,
                        _playedCard
                            ? _ctx.Strings.Get("iso_move_spent")
                            : _ctx.Strings.Format("iso_move_left", ("points", me.MovePoints.ToString())),
                        new Rectangle(0, 230, VirtualViewport.Width, 80),
                        _playedCard ? Color.Gray : Color.LightBlue, 0.36f);
                if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, EndTurnRect, _ctx.Strings.Get("iso_end_turn"), _tap))
                    NextTurn();
                if (_mode == Mode.PlayerTarget)
                {
                    int wanted = _selectedCard == null ? 1 : TargetsWanted(_selectedCard);
                    string prompt =
                        _targets.Count == 0 ? _ctx.Strings.Get("iso_pick_target")
                        : _targets.Count < wanted
                            ? _ctx.Strings.Format("iso_pick_more",
                                ("count", (wanted - _targets.Count).ToString()))
                            : _ctx.Strings.Get("iso_confirm_strike");
                    Ui.DrawTextCentered(batch, _ctx.Font, prompt,
                        new Rectangle(0, 1330, VirtualViewport.Width, 100), Color.OrangeRed, 0.44f);
                }
                DrawHand(batch);
                break;
            case Mode.EnemyTurn:
            case Mode.Acting:
                DrawTurnStrip(batch);
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

    private void DrawTurnStrip(SpriteBatch batch)
    {
        if (_order.Count == 0) return;
        var strip = string.Join("  >  ", _order.Select((inst, i) =>
            i == _turn ? "[" + inst.Name + "]" : inst.Alive ? inst.Name : "-"));
        Ui.DrawTextCentered(batch, _ctx.Font, strip,
            new Rectangle(0, 40, VirtualViewport.Width, 90), Color.White * 0.85f, 0.34f);
        if (Current != null)
            Ui.DrawTextCentered(batch, _ctx.Font,
                _ctx.Strings.Format("battle_turn", ("name", Current.Name)),
                new Rectangle(0, 140, VirtualViewport.Width, 90), Color.Gold, 0.46f);
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
        Ui.FillRect(batch, _ctx.Pixel, rect,
            _playedCard ? new Color(20, 20, 26, 250) : new Color(24, 24, 40, 250));
        var border = card == _selectedCard ? Color.Gold
            : _playedCard ? Color.White * 0.25f
            : hovered ? Color.White : Color.White * 0.5f;
        int bw = (int)(6 * s);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Y, rect.Width, bw), border);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Bottom - bw, rect.Width, bw), border);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Y, bw, rect.Height), border);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.Right - bw, rect.Y, bw, rect.Height), border);

        var ink = _playedCard ? Color.White * 0.4f : Color.White;
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
            _playedCard ? Color.Gold * 0.4f : Color.Gold, 0f, Vector2.Zero, 0.36f * s, SpriteEffects.None, 0f);
        batch.DrawString(_ctx.Font, _ctx.Strings.Format("iso_card_range", ("range", card.Range.ToString())),
            new Vector2(rect.X + 24 * s, rect.Bottom - 28 * s - size.Y),
            _playedCard ? Color.LightBlue * 0.4f : Color.LightBlue,
            0f, Vector2.Zero, 0.3f * s, SpriteEffects.None, 0f);
    }
}
