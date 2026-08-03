using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Screens;

/// <summary>
/// Turn-based combat. Turn order is rolled once at battle start: each side is
/// shuffled, then the sides alternate (random side first; leftovers append).
///
/// Playing a card runs a timed sequence: the casting sound plays and the
/// caster waits out the casting time, then a projectile flies (or the fighter
/// walks in) at the card's speed in feet per second, then each blow in the hit
/// sequence lands on its own schedule — sound, damage, and recoil together.
/// Melee fighters walk home afterward.
/// </summary>
public class BattleScreen : IScreen
{
    private enum Phase { Pause, PlayerChoose, PlayerTarget, Acting, EnemyTurn, Victory }
    private enum Act { Casting, Travel, MeleeWait, Hits, Return }

    /// <summary>Everything one attack needs, whether it came from a card or an enemy.</summary>
    private class ActionSpec
    {
        public CharacterInstance Actor = null!;
        public List<CharacterInstance> Victims = new();
        public CharacterInstance? Aim;          // single-projectile impact point
        public Delivery Delivery;
        public float CastSeconds, Speed, MeleeSeconds;
        public string? CastSound;
        public List<HitEvent> Hits = new();
        public int[] Damage = Array.Empty<int>();
        public string DamageType = "";
        public string ProjectileArt = "Projectile.png";
        public bool SingleProjectile;
    }

    private class Projectile
    {
        public Vector2 From, To;
        public float Rotation;   // aligned to the travel vector
    }

    private readonly GameContext _ctx;
    private readonly RoomScreen _room;
    private readonly List<CharacterInstance> _present;
    private readonly Texture2D _background;
    private readonly List<CharacterInstance> _order = new();
    private static readonly Random Rng = new();

    private Phase _phase = Phase.Pause;
    private float _timer = 1.2f;         // intro pause before the first turn
    private int _turn = -1;
    private List<Card> _hand = new();
    private Card? _selected;
    private readonly List<CharacterInstance> _targets = new();
    private string _toast;
    private float _toastTimer = 2f;
    private CharacterInstance? _dragging;
    private Point _pointer;
    private Point? _tap;

    // in-flight action
    private ActionSpec? _spec;
    private Act _act;
    private float _actT, _actDur;
    private Vector2 _walkVec;
    private readonly List<Projectile> _projectiles = new();
    private int _hitIndex;
    private float _hitTimer;

    /// <summary>Enemies have no card, so their approach uses these.</summary>
    private const float EnemySpeed = 8f;
    private const float EnemyMeleeWait = 0.15f;

    private static readonly Rectangle WinRect = new(1620, 1250, 600, 180);
    private const int CardW = 430, CardH = 600, CardGap = 30;
    private static readonly int CardRestY = VirtualViewport.Height - CardH / 2;
    private const float HoverScale = 1.3f;

    public BattleScreen(GameContext ctx, RoomScreen room,
        List<CharacterInstance> present, Texture2D background)
    {
        _ctx = ctx;
        _room = room;
        _present = present;
        _background = background;
        _toast = ctx.Strings.Get("battle_placeholder");
        foreach (var inst in _present) { inst.WalkOffset = Vector2.Zero; inst.ShakeTimer = 0f; }
        BuildOrder();
    }

    private List<CharacterInstance> Living(bool players) =>
        _present.Where(i => i.IsPlayer == players && i.Alive).ToList();

    private void BuildOrder()
    {
        var players = Living(true).OrderBy(_ => Rng.Next()).ToList();
        var enemies = Living(false).OrderBy(_ => Rng.Next()).ToList();
        var first = Rng.Next(2) == 0 ? players : enemies;
        var second = first == players ? enemies : players;
        for (int i = 0; i < Math.Max(first.Count, second.Count); i++)
        {
            if (i < first.Count) _order.Add(first[i]);
            if (i < second.Count) _order.Add(second[i]);
        }
    }

    private CharacterInstance Current => _order[_turn];

    private void NextTurn()
    {
        if (Living(false).Count == 0) { _phase = Phase.Victory; return; }
        if (Living(true).Count == 0) { _ctx.SwitchTo(new DeathScreen(_ctx)); return; }

        for (int step = 0; step < _order.Count; step++)
        {
            _turn = (_turn + 1) % _order.Count;
            if (_order[_turn].Alive) break;
        }
        if (Current.IsPlayer)
        {
            _hand = _ctx.Cards.HandFor(Current.Name);
            _selected = null;
            _targets.Clear();
            _phase = Phase.PlayerChoose;
        }
        else
        {
            _phase = Phase.EnemyTurn;
            _timer = 0.6f;
        }
    }

    public void Update(InputState input, float dt)
    {
        _pointer = input.PointerPos;
        _tap = input.Tap;
        if (_toastTimer > 0) _toastTimer -= dt;
        Formation.UpdateShakes(_present, dt);

        switch (_phase)
        {
            case Phase.Pause:
                _timer -= dt;
                if (_timer <= 0) NextTurn();
                break;
            case Phase.EnemyTurn:
                _timer -= dt;
                if (_timer <= 0) EnemyAct();
                break;
            case Phase.Acting:
                UpdateAction(dt);
                break;
            case Phase.PlayerChoose:
            case Phase.PlayerTarget:
                HandlePlayerInput(input);
                break;
        }
        HandleDrag(input);
    }

    private void HandleDrag(InputState input)
    {
        if (_dragging != null && input.Released is Point drop)
        {
            if (Formation.PlayerRowAt(drop) is int row)
                _dragging.Row = row;
            _dragging = null;
        }
    }

    /// <summary>
    /// How many enemies the player must click before the card fires. AoE
    /// normally needs none, but a Single Projectile card still needs somewhere
    /// to aim the shot.
    /// </summary>
    private int TargetsNeeded(Card card) => card.Kind switch
    {
        CardKind.AoEDamage => card.SingleProjectile && card.Delivery != Delivery.Instant ? 1 : 0,
        CardKind.SingleTargetHits => 1,
        _ => Math.Min(card.Targets, Math.Max(1, Living(false).Count)),
    };

    private void HandlePlayerInput(InputState input)
    {
        if (_tap is not Point press) return;

        var rects = HandRects();
        for (int i = 0; i < _hand.Count; i++)
            if (rects[i].Contains(press))
            {
                _selected = _hand[i];
                _targets.Clear();
                if (TargetsNeeded(_selected) == 0) PlayCard();
                else _phase = Phase.PlayerTarget;
                _tap = null;
                return;
            }

        var layout = Formation.Layout(_ctx, _present);

        if (_phase == Phase.PlayerTarget && _selected != null)
        {
            foreach (var (inst, rect) in layout)
                if (!inst.IsPlayer && rect.Contains(press) && !_targets.Contains(inst))
                {
                    _targets.Add(inst);
                    if (_targets.Count >= TargetsNeeded(_selected)) PlayCard();
                    _tap = null;
                    return;
                }
        }

        for (int i = layout.Count - 1; i >= 0; i--)
            if (layout[i].Inst.IsPlayer && layout[i].Rect.Contains(press))
            {
                _dragging = layout[i].Inst;
                _tap = null;
                return;
            }
    }

    private static Vector2 Center(Rectangle r) => new(r.Center.X, r.Center.Y);

    private void PlayCard()
    {
        if (_selected == null) return;
        var card = _selected;
        // AoE always damages everyone; the click only aims the shot
        var victims = card.Kind == CardKind.AoEDamage ? Living(false) : _targets.ToList();

        BeginAction(new ActionSpec
        {
            Actor = Current,
            Victims = victims,
            Aim = _targets.FirstOrDefault() ?? victims.FirstOrDefault(),
            Delivery = card.Delivery,
            CastSound = card.CastingSound,
            // "Use Sound Time" resolves here, once the sound is loaded
            CastSeconds = card.CastingTime ?? _ctx.Sounds.Duration(card.CastingSound),
            Speed = card.Speed,
            MeleeSeconds = card.MeleeTime,
            Hits = card.HitEvents,
            Damage = card.DamageSchedule(),
            DamageType = card.DamageType,
            ProjectileArt = card.ProjectileArt,
            SingleProjectile = card.SingleProjectile,
        });

        _selected = null;
        _targets.Clear();
    }

    private void EnemyAct()
    {
        var players = Living(true);
        if (players.Count == 0) { _phase = Phase.Pause; _timer = 0.2f; return; }

        var target = players[Rng.Next(players.Count)];
        BeginAction(new ActionSpec
        {
            Actor = Current,
            Victims = new List<CharacterInstance> { target },
            Aim = target,
            Delivery = Delivery.Melee,
            Speed = EnemySpeed,
            MeleeSeconds = EnemyMeleeWait,
            Hits = new List<HitEvent> { new() },
            Damage = new[] { Math.Max(1, Current.AttackDmg) },
            DamageType = Current.AttackType,
        });
    }

    private void BeginAction(ActionSpec spec)
    {
        _spec = spec;
        _projectiles.Clear();
        _hitIndex = 0;

        if (spec.Victims.Count == 0) { EndAction(); return; }

        _ctx.Sounds.Play(spec.CastSound);
        _phase = Phase.Acting;
        EnterAct(Act.Casting, spec.CastSeconds);
    }

    /// <summary>Travel time comes from distance and speed, not a fixed timer.</summary>
    private float TravelSeconds(Vector2 from, Vector2 to, float speed)
    {
        if (speed <= 0f) return 0f;
        float feet = Vector2.Distance(from, to) / Ruler.UnitPx;
        return feet / speed;
    }

    private void EnterAct(Act act, float duration)
    {
        _act = act;
        _actT = 0f;
        _actDur = Math.Max(0f, duration);

        if (act == Act.Hits && _spec != null)
            _hitTimer = _spec.Hits.Count > 0 ? _spec.Hits[0].Delay : 0f;
    }

    private void UpdateAction(float dt)
    {
        if (_spec == null) { EndAction(); return; }

        if (_act == Act.Hits) { UpdateHits(dt); return; }

        _actT += dt;
        float t = _actDur <= 0f ? 1f : MathHelper.Clamp(_actT / _actDur, 0f, 1f);

        if (_act == Act.Travel && _spec.Delivery == Delivery.Melee)
            _spec.Actor.WalkOffset = _walkVec * t;
        else if (_act == Act.Return)
            _spec.Actor.WalkOffset = _walkVec * (1f - t);

        if (t < 1f) return;

        switch (_act)
        {
            case Act.Casting:
                StartTravel();
                break;
            case Act.Travel:
                _projectiles.Clear();
                EnterAct(Act.Hits, 0f);
                break;
            case Act.MeleeWait:
                EnterAct(Act.Hits, 0f);
                break;
            case Act.Return:
                _spec.Actor.WalkOffset = Vector2.Zero;
                EndAction();
                break;
        }
    }

    private void StartTravel()
    {
        if (_spec == null) return;
        var from = Center(Formation.CurrentRect(_ctx, _present, _spec.Actor));

        if (_spec.Delivery == Delivery.Melee)
        {
            var targetRect = Formation.CurrentRect(_ctx, _present, _spec.Aim ?? _spec.Victims[0]);
            var to = Center(targetRect);
            // stop beside the target rather than on top of it
            float standoff = (targetRect.Width + 120) * (_spec.Actor.IsPlayer ? -0.5f : 0.5f);
            var stop = new Vector2(to.X + standoff, to.Y);
            _walkVec = stop - from;
            EnterAct(Act.Travel, TravelSeconds(from, stop, _spec.Speed));
        }
        else if (_spec.Delivery == Delivery.Ranged)
        {
            // one shot at the aim point, or one per target
            var shots = _spec.SingleProjectile
                ? new List<CharacterInstance> { _spec.Aim ?? _spec.Victims[0] }
                : _spec.Victims;
            float longest = 0f;
            foreach (var victim in shots)
            {
                var to = Center(Formation.CurrentRect(_ctx, _present, victim));
                _projectiles.Add(new Projectile
                {
                    From = from,
                    To = to,
                    // art is drawn pointing right; spin it onto the travel vector
                    Rotation = (float)Math.Atan2(to.Y - from.Y, to.X - from.X),
                });
                longest = Math.Max(longest, TravelSeconds(from, to, _spec.Speed));
            }
            EnterAct(Act.Travel, longest);
        }
        else
        {
            EnterAct(Act.Hits, 0f);
        }
    }

    /// <summary>Each blow waits out its own delay, then lands with its sound.</summary>
    private void UpdateHits(float dt)
    {
        if (_spec == null) { EndAction(); return; }
        _hitTimer -= dt;
        if (_hitTimer > 0f) return;

        var report = new StringBuilder();
        int dmg = _hitIndex < _spec.Damage.Length ? _spec.Damage[_hitIndex] : 0;
        _ctx.Sounds.Play(_hitIndex < _spec.Hits.Count ? _spec.Hits[_hitIndex].Sound : null);

        foreach (var victim in _spec.Victims.Where(v => v.Alive))
            Hit(victim, dmg, _spec.DamageType, report);
        if (report.Length > 0) Toast(report.ToString().TrimEnd());

        _hitIndex++;
        if (_hitIndex < _spec.Hits.Count)
        {
            _hitTimer = _spec.Hits[_hitIndex].Delay;
            return;
        }

        // melee walks home; ranged is done where it stands
        if (_spec.Delivery == Delivery.Melee && _walkVec != Vector2.Zero)
            EnterAct(Act.Return, TravelSeconds(Vector2.Zero, _walkVec, _spec.Speed));
        else
            EndAction();
    }

    private void EndAction()
    {
        if (_spec != null) _spec.Actor.WalkOffset = Vector2.Zero;
        _spec = null;
        _projectiles.Clear();
        _walkVec = Vector2.Zero;
        _phase = Phase.Pause;
        _timer = 0.5f;
    }

    private void Hit(CharacterInstance target, int dmg, string type, StringBuilder report)
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

    private void Toast(string text)
    {
        _toast = text;
        _toastTimer = 2.6f;
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

    public void Draw(SpriteBatch batch)
    {
        var screen = new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height);
        if (_background == _ctx.Pixel)
        {
            batch.Draw(_background, screen, new Color(30, 16, 16));
        }
        else
        {
            var size = AssetLoader.DisplaySize(_background, AssetKind.Background)
                * _ctx.Config.GlobalScale;
            batch.Draw(_background, Ui.FitCentered(size, screen), new Color(255, 220, 220));
        }

        var current = _turn >= 0 && _order[_turn].Alive ? _order[_turn] : null;
        Formation.DrawCast(batch, _ctx, _present, current, _targets, _dragging, _pointer);
        DrawProjectiles(batch);
        DrawTurnStrip(batch);

        if (_phase == Phase.PlayerTarget && _selected != null)
        {
            int left = TargetsNeeded(_selected) - _targets.Count;
            string prompt = left == 1
                ? _ctx.Strings.Get("battle_pick_target")
                : _ctx.Strings.Format("battle_pick_targets", ("count", left.ToString()));
            Ui.DrawTextCentered(batch, _ctx.Font, prompt,
                new Rectangle(0, 1300, VirtualViewport.Width, 120), Color.OrangeRed, 0.5f);
        }

        if (_toastTimer > 0)
            batch.DrawString(_ctx.Font, _toast, new Vector2(1200, 300), Color.White,
                0f, Vector2.Zero, 0.42f, SpriteEffects.None, 0f);

        // the hand draws last so cards may overlap the characters
        if (_phase is Phase.PlayerChoose or Phase.PlayerTarget)
            DrawHand(batch);

        if (_phase == Phase.Victory)
        {
            Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("battle_victory"),
                new Rectangle(0, 900, VirtualViewport.Width, 250), Color.Gold, 1.0f);
            if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, WinRect, _ctx.Strings.Get("battle_win"), _tap))
                _room.ResumeAfterBattle();
        }

        _tap = null;
    }

    private void DrawProjectiles(SpriteBatch batch)
    {
        if (_projectiles.Count == 0 || _spec == null) return;
        float t = _actDur <= 0f ? 1f : MathHelper.Clamp(_actT / _actDur, 0f, 1f);
        var tex = _ctx.Assets.LoadTexture($"Content/Images/Effects/{_spec.ProjectileArt}");
        var size = AssetLoader.DisplaySize(tex, AssetKind.Effect);
        var origin = new Vector2(tex.Width / 2f, tex.Height / 2f);
        var scale = new Vector2(size.X / tex.Width, size.Y / tex.Height);

        foreach (var p in _projectiles)
        {
            var pos = Vector2.Lerp(p.From, p.To, t);
            batch.Draw(tex, pos, null, Color.White, p.Rotation, origin, scale, SpriteEffects.None, 0f);
        }
    }

    private void DrawTurnStrip(SpriteBatch batch)
    {
        if (_turn < 0)
        {
            Ui.DrawTextCentered(batch, _ctx.Font, _toast,
                new Rectangle(0, 60, VirtualViewport.Width, 140), Color.White, 0.7f);
            return;
        }
        var strip = string.Join("  >  ", _order.Select((inst, i) =>
            (i == _turn ? "[" + inst.Name + "]" : inst.Alive ? inst.Name : "-")));
        Ui.DrawTextCentered(batch, _ctx.Font, strip,
            new Rectangle(0, 40, VirtualViewport.Width, 90), Color.White * 0.85f, 0.34f);
        Ui.DrawTextCentered(batch, _ctx.Font,
            _ctx.Strings.Format("battle_turn", ("name", Current.Name)),
            new Rectangle(0, 140, VirtualViewport.Width, 100), Color.Gold, 0.5f);
    }

    private void DrawHand(SpriteBatch batch)
    {
        var rects = HandRects();
        int living = Living(false).Count;
        int hovered = -1;
        for (int i = 0; i < _hand.Count; i++)
            if (rects[i].Contains(_pointer)) hovered = i;

        for (int i = 0; i < _hand.Count; i++)
            if (i != hovered)
                DrawCard(batch, _hand[i], rects[i], living, false);

        if (hovered >= 0)
        {
            int w = (int)(CardW * HoverScale), h = (int)(CardH * HoverScale);
            var lifted = new Rectangle(rects[hovered].Center.X - w / 2,
                VirtualViewport.Height - h - 30, w, h);
            DrawCard(batch, _hand[hovered], lifted, living, true);
        }
    }

    private void DrawCard(SpriteBatch batch, Card card, Rectangle rect, int livingEnemies, bool hovered)
    {
        float s = hovered ? HoverScale : 1f;
        Ui.FillRect(batch, _ctx.Pixel, rect, new Color(24, 24, 40, 250));
        var border = card == _selected ? Color.Gold : hovered ? Color.White : Color.White * 0.5f;
        int bw = (int)(6 * s);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Y, rect.Width, bw), border);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Bottom - bw, rect.Width, bw), border);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Y, bw, rect.Height), border);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.Right - bw, rect.Y, bw, rect.Height), border);

        Ui.DrawTextCentered(batch, _ctx.Font, card.Name,
            new Rectangle(rect.X, rect.Y + (int)(20 * s), rect.Width, (int)(90 * s)),
            Color.White, 0.42f * s);

        string body = Ui.Wrap(_ctx.Font, card.CardText, rect.Width - 60 * s, 0.32f * s);
        batch.DrawString(_ctx.Font, body, new Vector2(rect.X + 30 * s, rect.Y + 140 * s),
            Color.White * 0.9f, 0f, Vector2.Zero, 0.32f * s, SpriteEffects.None, 0f);

        string total = $"{card.TotalDamage(livingEnemies)} {card.DamageType}";
        var size = _ctx.Font.MeasureString(total) * (0.38f * s);
        batch.DrawString(_ctx.Font, total,
            new Vector2(rect.Right - 26 * s - size.X, rect.Bottom - 30 * s - size.Y),
            Color.Gold, 0f, Vector2.Zero, 0.38f * s, SpriteEffects.None, 0f);
    }
}
