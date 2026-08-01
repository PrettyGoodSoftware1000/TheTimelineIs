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
/// On a player character's turn, their class's cards sit half off the bottom
/// of the screen — hover to magnify one into full view, click to play it.
/// A card's [melee] tag walks the attacker to the target and [ranged] throws
/// a projectile; either way the hit lands after the card's travel time, when
/// the Hit sound plays and the target recoils.
/// </summary>
public class BattleScreen : IScreen
{
    private enum Phase { Pause, PlayerChoose, PlayerTarget, Acting, EnemyTurn, Victory }
    private enum Stage { Travel, Return }

    private class Projectile
    {
        public Vector2 From, To;
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
    private Stage _stage;
    private float _actionElapsed, _actionDuration;
    private CharacterInstance? _actor;
    private Vector2 _walkTo;
    private readonly List<Projectile> _projectiles = new();
    private Action? _onImpact;
    private string? _impactSound;

    private const float ReturnSeconds = 0.28f;
    private const float EnemyTravelSeconds = 0.35f;

    private static readonly Rectangle WinRect = new(1620, 1250, 600, 180);
    private const int CardW = 430, CardH = 600, CardGap = 30;
    /// <summary>Hand rests half below the bottom edge; hovering lifts a card into full view.</summary>
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
            _hand = _ctx.Cards.HandFor(_ctx.Classes.CardTagsFor(Current.Name));
            _selected = null;
            _targets.Clear();
            _phase = Phase.PlayerChoose;
        }
        else
        {
            _phase = Phase.EnemyTurn;
            _timer = 0.7f;
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

    private void HandlePlayerInput(InputState input)
    {
        if (_tap is not Point press) return;

        // card click (choose or re-choose)
        var rects = HandRects();
        for (int i = 0; i < _hand.Count; i++)
            if (rects[i].Contains(press))
            {
                _selected = _hand[i];
                _targets.Clear();
                if (_selected.Kind == CardKind.AoEDamage)
                    PlayCard();          // no targeting needed
                else
                    _phase = Phase.PlayerTarget;
                _tap = null;
                return;
            }

        var layout = Formation.Layout(_ctx, _present);

        // enemy click while targeting
        if (_phase == Phase.PlayerTarget && _selected != null)
        {
            foreach (var (inst, rect) in layout)
                if (!inst.IsPlayer && rect.Contains(press) && !_targets.Contains(inst))
                {
                    _targets.Add(inst);
                    int needed = _selected.Kind == CardKind.SingleTargetHits
                        ? 1 : Math.Min(_selected.Targets, Living(false).Count);
                    if (_targets.Count >= needed)
                        PlayCard();
                    _tap = null;
                    return;
                }
        }

        // player sprite click: start a row drag
        for (int i = layout.Count - 1; i >= 0; i--)
            if (layout[i].Inst.IsPlayer && layout[i].Rect.Contains(press))
            {
                _dragging = layout[i].Inst;
                _tap = null;
                return;
            }
    }

    private static Vector2 Center(Rectangle r) => new(r.Center.X, r.Center.Y);

    /// <summary>
    /// Starts a card: activation sound now, then the walk or projectile, then
    /// the impact after the card's travel time. Cards with no travel time
    /// resolve immediately.
    /// </summary>
    private void PlayCard()
    {
        if (_selected == null) return;
        var card = _selected;
        var victims = card.Kind == CardKind.AoEDamage ? Living(false) : _targets.ToList();
        _ctx.Sounds.Play(card.ActivationSound);

        void Impact() => ApplyCardDamage(card, victims);

        BeginAction(Current, victims, card.Delivery, card.TravelSeconds, card.HitSound, Impact);
        _selected = null;
        _targets.Clear();
    }

    private void BeginAction(CharacterInstance actor, List<CharacterInstance> victims,
        Delivery delivery, float travel, string? hitSound, Action onImpact)
    {
        _projectiles.Clear();
        _actor = null;
        _onImpact = onImpact;
        _impactSound = hitSound;

        if (travel <= 0f || delivery == Delivery.Instant || victims.Count == 0)
        {
            Impact();
            return;
        }

        var from = Center(Formation.CurrentRect(_ctx, _present, actor));
        _actionElapsed = 0f;
        _actionDuration = travel;
        _stage = Stage.Travel;
        _phase = Phase.Acting;

        if (delivery == Delivery.Melee)
        {
            // stop just short of the first victim rather than standing on them
            var targetRect = Formation.CurrentRect(_ctx, _present, victims[0]);
            var to = Center(targetRect);
            float standoff = (targetRect.Width + 120) * (actor.IsPlayer ? -0.5f : 0.5f);
            _actor = actor;
            _walkTo = new Vector2(to.X + standoff - from.X, to.Y - from.Y);
        }
        else
        {
            foreach (var victim in victims)
                _projectiles.Add(new Projectile
                {
                    From = from,
                    To = Center(Formation.CurrentRect(_ctx, _present, victim)),
                });
        }
    }

    private void UpdateAction(float dt)
    {
        _actionElapsed += dt;
        float t = MathHelper.Clamp(_actionElapsed / Math.Max(0.0001f, _actionDuration), 0f, 1f);

        if (_actor != null)
            _actor.WalkOffset = _stage == Stage.Travel ? _walkTo * t : _walkTo * (1f - t);

        if (t < 1f) return;

        if (_stage == Stage.Travel)
        {
            Impact();
        }
        else
        {
            if (_actor != null) _actor.WalkOffset = Vector2.Zero;
            _actor = null;
            _phase = Phase.Pause;
            _timer = 0.5f;
        }
    }

    /// <summary>The moment the blow lands: sound, damage, recoil, then walk back.</summary>
    private void Impact()
    {
        _ctx.Sounds.Play(_impactSound);
        _projectiles.Clear();
        _onImpact?.Invoke();
        _onImpact = null;

        if (_actor != null)
        {
            _stage = Stage.Return;
            _actionElapsed = 0f;
            _actionDuration = ReturnSeconds;
            _phase = Phase.Acting;
        }
        else
        {
            _phase = Phase.Pause;
            _timer = 0.7f;
        }
    }

    private void ApplyCardDamage(Card card, List<CharacterInstance> victims)
    {
        var report = new StringBuilder();
        switch (card.Kind)
        {
            case CardKind.AoEDamage:
                foreach (var enemy in victims.Where(v => v.Alive))
                    Hit(enemy, card.Damage, card.DamageType, report);
                break;
            case CardKind.SingleTargetHits:
                if (victims.Count > 0)
                    Hit(victims[0], card.Damage * card.Hits, card.DamageType, report);
                break;
            case CardKind.MultiTarget:
                foreach (var target in victims)
                    Hit(target, card.Damage, card.DamageType, report);
                break;
        }
        Toast(report.ToString().TrimEnd());
    }

    private void Hit(CharacterInstance target, int dmg, string type, StringBuilder report)
    {
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
        var players = Living(true);
        if (players.Count == 0) { _phase = Phase.Pause; _timer = 0.2f; return; }

        var attacker = Current;
        var target = players[Rng.Next(players.Count)];
        int dmg = Math.Max(1, attacker.AttackDmg);

        void Impact()
        {
            var report = new StringBuilder();
            target.Hp -= dmg;
            target.ShakeTimer = Formation.ShakeDuration;
            report.Append(_ctx.Strings.Format("battle_enemy_hit",
                ("attacker", attacker.Name), ("target", target.Name),
                ("dmg", dmg.ToString()), ("type", attacker.AttackType)));
            if (target.Hp <= 0 && target.Alive)
            {
                target.Hp = 0;
                target.Alive = false;
                report.Append('\n').Append(_ctx.Strings.Format("battle_down", ("name", target.Name)));
            }
            Toast(report.ToString());
        }

        BeginAction(attacker, new List<CharacterInstance> { target },
            Delivery.Melee, EnemyTravelSeconds, null, Impact);
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
            int needed = _selected.Kind == CardKind.SingleTargetHits
                ? 1 : Math.Min(_selected.Targets, Living(false).Count);
            string prompt = needed - _targets.Count == 1
                ? _ctx.Strings.Get("battle_pick_target")
                : _ctx.Strings.Format("battle_pick_targets",
                    ("count", (needed - _targets.Count).ToString()));
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
        if (_projectiles.Count == 0) return;
        float t = MathHelper.Clamp(_actionElapsed / Math.Max(0.0001f, _actionDuration), 0f, 1f);
        var tex = _ctx.Assets.LoadTexture("Content/Images/Effects/Projectile.png");
        var size = AssetLoader.DisplaySize(tex, AssetKind.Effect);
        foreach (var p in _projectiles)
        {
            var pos = Vector2.Lerp(p.From, p.To, t);
            batch.Draw(tex, new Rectangle((int)(pos.X - size.X / 2), (int)(pos.Y - size.Y / 2),
                (int)size.X, (int)size.Y), Color.White);
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

        // the hovered card draws last, lifted into full view and magnified
        if (hovered >= 0)
        {
            int w = (int)(CardW * HoverScale), h = (int)(CardH * HoverScale);
            var lifted = new Rectangle(
                rects[hovered].Center.X - w / 2,
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
        batch.DrawString(_ctx.Font, body,
            new Vector2(rect.X + 30 * s, rect.Y + 140 * s),
            Color.White * 0.9f, 0f, Vector2.Zero, 0.32f * s, SpriteEffects.None, 0f);

        // dynamic bottom-right: total damage against the current room
        string total = $"{card.TotalDamage(livingEnemies)} {card.DamageType}";
        var size = _ctx.Font.MeasureString(total) * (0.38f * s);
        batch.DrawString(_ctx.Font, total,
            new Vector2(rect.Right - 26 * s - size.X, rect.Bottom - 30 * s - size.Y),
            Color.Gold, 0f, Vector2.Zero, 0.38f * s, SpriteEffects.None, 0f);
    }
}
