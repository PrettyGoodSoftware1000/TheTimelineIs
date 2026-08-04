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
/// order rolls (sides shuffled, alternating). On a turn a character spends
/// Movement points (orthogonal 1, diagonal 2, +1 per foot climbed, max 4 ft
/// up) shown as a 20%-blue overlay, and may play one card; melee reaches 1
/// tile, ranged cards their Range. Enemies chase the nearest player and use
/// their basic attack. Doors open when clicked from beside them and reveal
/// the room behind; enemies see through open doors. Clearing every enemy
/// completes the level.
/// </summary>
public class IsoLevelScreen : IScreen
{
    private enum Mode { Explore, FreeMove, PlayerTurn, PlayerTarget, EnemyTurn, Acting, Victory }
    private enum Act { Casting, Projectile, MeleeWait, Hits, EnemyWindup }

    private const int AggroTiles = 15;
    private const float WalkTilesPerSec = 5f;
    private const int DoorReach = 2;             // orth or diagonal neighbor

    private readonly GameContext _ctx;
    private readonly LevelData _level;
    private readonly List<CharacterInstance> _party = new();
    private readonly List<CharacterInstance> _enemies = new();
    private readonly HashSet<string> _revealed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<CharacterInstance> _aggroed = new();
    private readonly List<CharacterInstance> _order = new();
    private static readonly Random Rng = new();

    private Mode _mode = Mode.Explore;
    private int _turn = -1;
    private CharacterInstance? _selected;        // explore / free-move selection
    private readonly HashSet<CharacterInstance> _freeMovers = new();
    private bool _playedCard;
    private List<Card> _hand = new();
    private Card? _selectedCard;
    private readonly List<CharacterInstance> _targets = new();

    private Vector2 _camera;
    private Vector2 _baseOrigin;
    private Point _pointer;
    private Point? _tap;
    private string _toast = "";
    private float _toastTimer;

    // walking animation
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

    private static readonly Rectangle EndTurnRect = new(3280, 60, 500, 160);
    private static readonly Rectangle DoneRect = new(3280, 60, 500, 160);
    private static readonly Rectangle WinRect = new(1620, 1250, 600, 180);
    private const int CardW = 400, CardH = 560, CardGap = 26;
    private static readonly int CardRestY = VirtualViewport.Height - CardH / 2;
    private const float HoverScale = 1.3f;

    public IsoLevelScreen(GameContext ctx, string levelName)
    {
        _ctx = ctx;
        _level = LevelData.Load(levelName);
        SpawnParty();
        SpawnEnemies();
        foreach (var start in _level.PlayerStarts.Take(_party.Count))
            if (_level.BlockAt(start) is LevelBlock b)
                _revealed.Add(b.Room);
        if (_revealed.Count == 0 && _level.Blocks.Count > 0)
            _revealed.Add(_level.Blocks.Values.First().Room);

        // center the camera on the party
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
            if (def == null) continue;   // the validator already complained
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

    /// <summary>Least-used sprite variant first, like the side-view game.</summary>
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

    private void Toast(string text) { _toast = text; _toastTimer = 3f; }

    /// <summary>Where a character's feet are on screen right now (walking included).</summary>
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

        if (_walker != null) { UpdateWalk(dt); return; }

        switch (_mode)
        {
            case Mode.Acting:
                UpdateAction(dt);
                break;
            case Mode.EnemyTurn:
                EnemyAct();
                break;
            case Mode.Explore:
            case Mode.FreeMove:
            case Mode.PlayerTurn:
            case Mode.PlayerTarget:
                HandleClicks();
                break;
        }
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

            if (_mode == Mode.Explore && _walker.IsPlayer && CheckAggro(_walker))
            {
                _walkPath.Clear();   // spotted: stop where they stand
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

    /// <summary>True when this character is now within sight of a revealed enemy.</summary>
    private bool CheckAggro(CharacterInstance mover)
    {
        var seen = VisibleEnemies.Where(e =>
            _party.Any(p => p.Alive && IsoMath.GridDistance(Tile(p), Tile(e)) <= AggroTiles)).ToList();
        if (seen.Count == 0) return false;

        foreach (var e in seen) _aggroed.Add(e);
        if (_mode is Mode.Explore)
        {
            // the others get a free positioning move before turn order rolls
            _freeMovers.Clear();
            foreach (var p in LivingParty.Where(p => p != mover))
            {
                p.MovePoints = p.MoveMax;
                _freeMovers.Add(p);
            }
            _mode = Mode.FreeMove;
            _selected = _freeMovers.FirstOrDefault();
            Toast(_ctx.Strings.Get("iso_spotted"));
        }
        return true;
    }

    private void StartCombat()
    {
        _order.Clear();
        var players = LivingParty.OrderBy(_ => Rng.Next()).ToList();
        var foes = _aggroed.Where(e => e.Alive).OrderBy(_ => Rng.Next()).ToList();
        var first = Rng.Next(2) == 0 ? players : foes.Cast<CharacterInstance>().ToList();
        var second = ReferenceEquals(first, players) ? foes : players;
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
        if (LivingParty.Count == 0) { _ctx.SwitchTo(new DeathScreen(_ctx)); return; }
        if (!_aggroed.Any(e => e.Alive))
        {
            // combat over; the level itself completes when every enemy is gone
            _aggroed.Clear();
            _order.Clear();
            _turn = -1;
            if (_enemies.All(e => !e.Alive)) { _mode = Mode.Victory; return; }
            _mode = Mode.Explore;
            Toast(_ctx.Strings.Get("iso_clear"));
            return;
        }

        // late arrivals (revealed mid-fight) join the end of the order
        foreach (var e in _aggroed.Where(e => e.Alive && !_order.Contains(e)))
            _order.Add(e);

        for (int step = 0; step < _order.Count; step++)
        {
            _turn = (_turn + 1) % _order.Count;
            if (_order[_turn].Alive) break;
        }
        var current = Current!;
        current.MovePoints = current.MoveMax;
        if (current.IsPlayer)
        {
            _playedCard = false;
            _hand = _ctx.Cards.HandFor(_ctx.Classes.CardTagsFor(current.Name));
            _selectedCard = null;
            _targets.Clear();
            _mode = Mode.PlayerTurn;
        }
        else
        {
            _mode = Mode.EnemyTurn;
        }
    }

    // ---------------- input ----------------

    private void HandleClicks()
    {
        if (_tap is not Point press) return;
        _tap = null;
        var screen = press.ToVector2();

        // buttons are handled in Draw (they need to render anyway); cards first
        if (_mode is Mode.PlayerTurn or Mode.PlayerTarget && HandleCardClick(press)) return;
        if (HitButton(press)) return;

        // click a character?
        foreach (var c in Everyone.Reverse())
        {
            if (!c.Alive) continue;
            if (!SpriteRect(c).Contains(press)) continue;

            if (c.IsPlayer)
            {
                if (_mode == Mode.Explore) { _selected = c; return; }
                if (_mode == Mode.FreeMove && _freeMovers.Contains(c)) { _selected = c; return; }
            }
            else if (_mode == Mode.PlayerTarget && _selectedCard != null)
            {
                TryTarget(c);
                return;
            }
            return;
        }

        // click a door?
        if (FindTileAt(screen) is Point tile)
        {
            if (_level.DoorAt(tile) is LevelDoor door && !door.Open &&
                _mode is Mode.Explore &&
                LivingParty.Any(p => IsoMath.GridDistance(Tile(p), tile) <= DoorReach))
            {
                OpenDoor(door);
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
        Toast(_ctx.Strings.Get("iso_door_open"));
        // enemies see through open doors — the opener is the trigger
        var nearest = LivingParty.OrderBy(p => IsoMath.GridDistance(Tile(p), new Point(door.X, door.Y))).First();
        CheckAggro(nearest);
    }

    private void HandleTileClick(Point tile)
    {
        var mover = _mode switch
        {
            Mode.Explore => _selected,
            Mode.FreeMove => _selected != null && _freeMovers.Contains(_selected) ? _selected : null,
            Mode.PlayerTurn => Current,
            _ => null,
        };
        if (mover == null || !mover.Alive) return;

        int budget = _mode == Mode.Explore ? 9999 : mover.MovePoints;
        var (cost, parent) = Pathfinder.Reachable(_level, Tile(mover), budget, _revealed, OccupiedExcept(mover));
        if (!cost.TryGetValue(tile, out int spent)) return;

        if (_mode != Mode.Explore) mover.MovePoints -= spent;
        _walker = mover;
        _walkFrom = Tile(mover);
        _walkPath = Pathfinder.PathTo(parent, _walkFrom, tile);
        _walkT = 0f;
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
                _mode = Mode.PlayerTarget;
                return true;
            }
        return false;
    }

    private void TryTarget(CharacterInstance enemy)
    {
        var card = _selectedCard!;
        var me = Current!;
        if (IsoMath.GridDistance(Tile(me), Tile(enemy)) > card.Range)
        {
            Toast(_ctx.Strings.Get("iso_out_of_range"));
            return;
        }
        if (_targets.Contains(enemy)) return;
        _targets.Add(enemy);

        int needed = card.Kind switch
        {
            CardKind.MultiTarget => Math.Min(card.Targets, EnemiesInRange(card).Count),
            _ => 1,
        };
        if (_targets.Count >= needed)
            PlayCard();
    }

    private List<CharacterInstance> EnemiesInRange(Card card)
    {
        var me = Current!;
        return VisibleEnemies.Where(e =>
            IsoMath.GridDistance(Tile(me), Tile(e)) <= card.Range).ToList();
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
                _mode = Mode.PlayerTurn;
                NextTurn();
                return true;
        }
        return false;
    }

    // ---------------- card + enemy actions ----------------

    private void PlayCard()
    {
        var card = _selectedCard!;
        _actor = Current;
        _actingCard = card;
        // AoE damages every visible enemy in range; the click only aimed the shot
        _victims = card.Kind == CardKind.AoEDamage ? EnemiesInRange(card) : _targets.ToList();
        _selectedCard = null;
        _targets.Clear();
        _playedCard = true;

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
            _hitTimer = _actingCard != null && _actingCard.HitEvents.Count > 0
                ? _actingCard.HitEvents[0].Delay : 0f;
    }

    private void UpdateAction(float dt)
    {
        if (_act == Act.Hits) { UpdateHits(dt); return; }

        _actT += dt;
        if (_actDur > 0 && _actT < _actDur) return;

        switch (_act)
        {
            case Act.Casting when _actingCard is { Delivery: Delivery.Ranged }:
                var aim = _victims.FirstOrDefault();
                if (aim == null) { FinishAction(); return; }
                _projFrom = FootOf(_actor!) - new Vector2(0, 160);
                _projTo = FootOf(aim) - new Vector2(0, 160);
                _projRotation = (float)Math.Atan2(_projTo.Y - _projFrom.Y, _projTo.X - _projFrom.X);
                float tiles = IsoMath.GridDistance(Tile(_actor!), Tile(aim));
                EnterAct(Act.Projectile, tiles / Math.Max(1f, _actingCard.Speed));
                break;
            case Act.Casting when _actingCard is { Delivery: Delivery.Melee } meleeCard:
                EnterAct(Act.MeleeWait, meleeCard.MeleeTime);
                break;
            case Act.Casting:
                _hitIndex = 0;
                EnterAct(Act.Hits, 0f);
                break;
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
        foreach (var v in _victims.Where(v => v.Alive))
            ApplyHit(v, dmg, card.DamageType, report);
        if (report.Length > 0) Toast(report.ToString().TrimEnd());

        _hitIndex++;
        if (_hitIndex < card.HitEvents.Count)
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
        // the turn continues: leftover movement may still be spent
        _mode = Mode.PlayerTurn;
        if (LivingParty.Count == 0) { _ctx.SwitchTo(new DeathScreen(_ctx)); return; }
        if (!_aggroed.Any(e => e.Alive)) NextTurn();
    }

    private void ApplyHit(CharacterInstance target, int dmg, string type, StringBuilder report)
    {
        if (dmg <= 0) return;
        target.Hp -= dmg;
        target.ShakeTimer = Formation.ShakeDuration;
        report.AppendLine(_ctx.Strings.Format("battle_hit",
            ("target", target.Name), ("dmg", dmg.ToString()), ("type", type)));
        if (target.Hp <= 0 && target.Alive)
        {
            target.Hp = 0;
            target.Alive = false;
            report.AppendLine(_ctx.Strings.Format("battle_down", ("name", target.Name)));
        }
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
        else
        {
            NextTurn();
        }
    }

    private void ResolveEnemyHit()
    {
        var me = _actor!;
        var target = _victims.FirstOrDefault();
        if (target != null && target.Alive)
        {
            _ctx.Sounds.Play(me.AttackSound);
            var report = new StringBuilder();
            target.Hp -= me.AttackDmg;
            target.ShakeTimer = Formation.ShakeDuration;
            report.Append(_ctx.Strings.Format("battle_enemy_hit",
                ("attacker", me.Name), ("target", target.Name),
                ("dmg", me.AttackDmg.ToString()), ("type", me.AttackType)));
            if (target.Hp <= 0 && target.Alive)
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
        // front-most first, so a raised block wins over the tile behind it
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
        // the void: solid black
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height), Color.Black);

        // movement overlay set for the active mover
        Dictionary<Point, int>? overlay = null;
        var active = _mode switch
        {
            Mode.FreeMove when _selected != null && _freeMovers.Contains(_selected) => _selected,
            Mode.PlayerTurn or Mode.PlayerTarget => Current,
            _ => null,
        };
        if (active != null && _walker == null)
            overlay = Pathfinder.Reachable(_level, Tile(active), active.MovePoints,
                _revealed, OccupiedExcept(active)).Cost;

        // painter's pass: blocks back-to-front, occupants right after their tile
        var byTile = new Dictionary<Point, List<CharacterInstance>>();
        foreach (var c in Everyone.Where(c => c.Alive))
        {
            var key = c == _walker && _walkPath.Count > 0 ? _walkPath[0] : Tile(c);
            if (!byTile.TryGetValue(key, out var list))
                byTile[key] = list = new List<CharacterInstance>();
            list.Add(c);
        }

        foreach (var block in _level.Blocks.Values
                     .Where(b => _revealed.Contains(b.Room))
                     .OrderBy(b => b.X + b.Y).ThenBy(b => b.X))
        {
            DrawBlock(batch, block);
            var tile = new Point(block.X, block.Y);

            if (overlay != null && overlay.ContainsKey(tile))
                DrawDiamond(batch, tile, block.Height, Color.Blue * 0.2f);
            if (_mode == Mode.PlayerTarget && _selectedCard != null)
            {
                var victim = byTile.TryGetValue(tile, out var here)
                    ? here.FirstOrDefault(c => !c.IsPlayer) : null;
                if (victim != null &&
                    IsoMath.GridDistance(Tile(Current!), tile) <= _selectedCard.Range)
                    DrawDiamond(batch, tile, block.Height, Color.OrangeRed * 0.25f);
            }

            if (_level.DoorAt(tile) is LevelDoor door)
                DrawBillboard(batch, "Content/Images/Decorations/Door.png", tile, block.Height,
                    door.Open ? Color.White * 0.35f : Color.White);
            if (_level.DecorationAt(tile) is LevelDecoration deco)
                DrawBillboard(batch, BlockCatalog.DecorationPath(deco.File), tile, block.Height, Color.White);

            if (byTile.TryGetValue(tile, out var standing))
                foreach (var c in standing)
                    DrawCharacter(batch, c);
        }

        DrawProjectile(batch);
        DrawHud(batch);
        _tap = null;
    }

    private void DrawBlock(SpriteBatch batch, LevelBlock block)
    {
        var top = IsoMath.ToScreen(block.X, block.Y, block.Height, Origin);
        var side = _ctx.Assets.LoadTexture(BlockCatalog.SidePath(block.Type));
        for (int f = 0; f < block.Height; f++)
            batch.Draw(side, new Rectangle((int)(top.X - IsoMath.TileW / 2f),
                (int)(top.Y + f * IsoMath.FootPx), IsoMath.TileW, IsoMath.FootPx), Color.White);
        // ground-level tiles still get one thin lip so edges read against the void
        if (block.Height == 0)
            batch.Draw(side, new Rectangle((int)(top.X - IsoMath.TileW / 2f),
                (int)top.Y, IsoMath.TileW, IsoMath.FootPx / 2), Color.White * 0.8f);
        batch.Draw(_ctx.Assets.LoadTexture(BlockCatalog.TopPath(block.Type)),
            new Rectangle((int)(top.X - IsoMath.TileW / 2f), (int)(top.Y - IsoMath.TileH / 2f),
                IsoMath.TileW, IsoMath.TileH), Color.White);
    }

    private void DrawDiamond(SpriteBatch batch, Point tile, int height, Color color)
    {
        var c = IsoMath.ToScreen(tile.X, tile.Y, height, Origin);
        batch.Draw(_ctx.Assets.LoadTexture("Content/Images/Blocks/OverlayTop.png"),
            new Rectangle((int)(c.X - IsoMath.TileW / 2f), (int)(c.Y - IsoMath.TileH / 2f),
                IsoMath.TileW, IsoMath.TileH), color);
    }

    private void DrawBillboard(SpriteBatch batch, string path, Point tile, int height, Color tint)
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
        if (c == Current && _mode is Mode.PlayerTurn or Mode.PlayerTarget or Mode.Acting or Mode.EnemyTurn)
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Bottom + 6, rect.Width, 8), Color.Gold);

        int barW = Math.Min(170, rect.Width);
        var back = new Rectangle(rect.X + (rect.Width - barW) / 2, rect.Bottom + 20, barW, 13);
        Ui.FillRect(batch, _ctx.Pixel, back, Color.Black * 0.65f);
        int fill = (int)(barW * Math.Clamp(c.Hp / (float)c.MaxHp, 0f, 1f));
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(back.X, back.Y, fill, back.Height),
            c.IsPlayer ? new Color(70, 190, 70) : new Color(200, 60, 60));
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

    private void DrawHud(SpriteBatch batch)
    {
        if (_toastTimer > 0)
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
                        _ctx.Strings.Format("iso_move_left", ("points", me.MovePoints.ToString())),
                        new Rectangle(0, 230, VirtualViewport.Width, 80), Color.LightBlue, 0.36f);
                if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, EndTurnRect, _ctx.Strings.Get("iso_end_turn"), _tap))
                    NextTurn();
                if (_mode == Mode.PlayerTarget)
                    Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("battle_pick_target"),
                        new Rectangle(0, 1300, VirtualViewport.Width, 100), Color.OrangeRed, 0.5f);
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
                    _ctx.State.EndMission(completed: false); // level name already recorded
                    _ctx.SwitchTo(new MapScreen(_ctx));
                }
                break;
        }
    }

    private void DrawTurnStrip(SpriteBatch batch)
    {
        if (_order.Count == 0) return;
        var strip = string.Join("  >  ", _order.Select((inst, i) =>
            (i == _turn ? "[" + inst.Name + "]" : inst.Alive ? inst.Name : "-")));
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
        Ui.FillRect(batch, _ctx.Pixel, rect, new Color(24, 24, 40, 250));
        var border = card == _selectedCard ? Color.Gold : hovered ? Color.White : Color.White * 0.5f;
        int bw = (int)(6 * s);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Y, rect.Width, bw), border);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Bottom - bw, rect.Width, bw), border);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Y, bw, rect.Height), border);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.Right - bw, rect.Y, bw, rect.Height), border);

        Ui.DrawTextCentered(batch, _ctx.Font, card.Name,
            new Rectangle(rect.X, rect.Y + (int)(18 * s), rect.Width, (int)(80 * s)), Color.White, 0.4f * s);
        string body = Ui.Wrap(_ctx.Font, card.CardText, rect.Width - 56 * s, 0.3f * s);
        batch.DrawString(_ctx.Font, body, new Vector2(rect.X + 28 * s, rect.Y + 120 * s),
            Color.White * 0.9f, 0f, Vector2.Zero, 0.3f * s, SpriteEffects.None, 0f);

        int living = VisibleEnemies.Count;
        string total = $"{card.TotalDamage(living)} {card.DamageType}";
        var size = _ctx.Font.MeasureString(total) * (0.36f * s);
        batch.DrawString(_ctx.Font, total,
            new Vector2(rect.Right - 24 * s - size.X, rect.Bottom - 28 * s - size.Y),
            Color.Gold, 0f, Vector2.Zero, 0.36f * s, SpriteEffects.None, 0f);
        string range = _ctx.Strings.Format("iso_card_range", ("range", card.Range.ToString()));
        batch.DrawString(_ctx.Font, range, new Vector2(rect.X + 24 * s, rect.Bottom - 28 * s - size.Y),
            Color.LightBlue, 0f, Vector2.Zero, 0.3f * s, SpriteEffects.None, 0f);
    }
}
