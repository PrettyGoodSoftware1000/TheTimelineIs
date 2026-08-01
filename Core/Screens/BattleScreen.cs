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
/// On a player character's turn, their class's cards appear at the bottom —
/// click one, pick targets if it needs them, and it resolves. Enemies attack
/// a random living player character. All enemies dead = victory and the room
/// continues; all player characters dead = death screen and reload.
/// </summary>
public class BattleScreen : IScreen
{
    private enum Phase { Pause, PlayerChoose, PlayerTarget, EnemyTurn, Victory }

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

    private static readonly Rectangle WinRect = new(1620, 1250, 600, 180);
    private const int CardW = 430, CardH = 600, CardGap = 30, CardY = 1530;

    public BattleScreen(GameContext ctx, RoomScreen room,
        List<CharacterInstance> present, Texture2D background)
    {
        _ctx = ctx;
        _room = room;
        _present = present;
        _background = background;
        _toast = ctx.Strings.Get("battle_placeholder");
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
            _timer = 1.0f;
        }
    }

    public void Update(InputState input, float dt)
    {
        _pointer = input.PointerPos;
        _tap = input.Tap;
        if (_toastTimer > 0) _toastTimer -= dt;

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
                    ResolveCard();       // no targeting needed
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
                        ResolveCard();
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

    private void ResolveCard()
    {
        if (_selected == null) return;
        var card = _selected;
        var report = new StringBuilder();

        void Hit(CharacterInstance target, int dmg)
        {
            target.Hp -= dmg;
            report.AppendLine(_ctx.Strings.Format("battle_hit",
                ("target", target.Name), ("dmg", dmg.ToString()), ("type", card.DamageType)));
            if (target.Hp <= 0 && target.Alive)
            {
                target.Hp = 0;
                target.Alive = false;
                report.AppendLine(_ctx.Strings.Format("battle_down", ("name", target.Name)));
            }
        }

        switch (card.Kind)
        {
            case CardKind.AoEDamage:
                foreach (var enemy in Living(false))
                    Hit(enemy, card.Damage);
                break;
            case CardKind.SingleTargetHits:
                Hit(_targets[0], card.Damage * card.Hits);
                break;
            case CardKind.MultiTarget:
                foreach (var target in _targets)
                    Hit(target, card.Damage);
                break;
        }

        Toast(report.ToString().TrimEnd());
        _selected = null;
        _targets.Clear();
        _phase = Phase.Pause;
        _timer = 0.9f;
    }

    private void EnemyAct()
    {
        var players = Living(true);
        var target = players[Rng.Next(players.Count)];
        int dmg = Math.Max(1, Current.AttackDmg);
        target.Hp -= dmg;
        var report = new StringBuilder(_ctx.Strings.Format("battle_enemy_hit",
            ("attacker", Current.Name), ("target", target.Name),
            ("dmg", dmg.ToString()), ("type", Current.AttackType)));
        if (target.Hp <= 0)
        {
            target.Hp = 0;
            target.Alive = false;
            report.Append('\n').Append(_ctx.Strings.Format("battle_down", ("name", target.Name)));
        }
        Toast(report.ToString());
        _phase = Phase.Pause;
        _timer = 0.9f;
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
            rects.Add(new Rectangle(x0 + i * (CardW + CardGap), CardY, CardW, CardH));
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

        DrawTurnStrip(batch);

        if (_phase is Phase.PlayerChoose or Phase.PlayerTarget)
            DrawHand(batch);

        if (_phase == Phase.PlayerTarget && _selected != null)
        {
            int needed = _selected.Kind == CardKind.SingleTargetHits
                ? 1 : Math.Min(_selected.Targets, Living(false).Count);
            string prompt = needed - _targets.Count == 1
                ? _ctx.Strings.Get("battle_pick_target")
                : _ctx.Strings.Format("battle_pick_targets",
                    ("count", (needed - _targets.Count).ToString()));
            Ui.DrawTextCentered(batch, _ctx.Font, prompt,
                new Rectangle(0, 1380, VirtualViewport.Width, 120), Color.OrangeRed, 0.5f);
        }

        if (_toastTimer > 0)
            batch.DrawString(_ctx.Font, _toast, new Vector2(1200, 260), Color.White,
                0f, Vector2.Zero, 0.42f, SpriteEffects.None, 0f);

        if (_phase == Phase.Victory)
        {
            Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("battle_victory"),
                new Rectangle(0, 900, VirtualViewport.Width, 250), Color.Gold, 1.0f);
            if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, WinRect, _ctx.Strings.Get("battle_win"), _tap))
                _room.ResumeAfterBattle();
        }

        _tap = null;
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
        for (int i = 0; i < _hand.Count; i++)
        {
            var card = _hand[i];
            var rect = rects[i];
            Ui.FillRect(batch, _ctx.Pixel, rect, new Color(24, 24, 40, 245));
            var border = card == _selected ? Color.Gold : Color.White * 0.5f;
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Y, rect.Width, 6), border);
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Bottom - 6, rect.Width, 6), border);
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Y, 6, rect.Height), border);
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.Right - 6, rect.Y, 6, rect.Height), border);

            Ui.DrawTextCentered(batch, _ctx.Font, card.Name,
                new Rectangle(rect.X, rect.Y + 20, rect.Width, 90), Color.White, 0.42f);

            string body = Ui.Wrap(_ctx.Font, card.CardText, rect.Width - 60, 0.32f);
            batch.DrawString(_ctx.Font, body, new Vector2(rect.X + 30, rect.Y + 140),
                Color.White * 0.9f, 0f, Vector2.Zero, 0.32f, SpriteEffects.None, 0f);

            // dynamic bottom-right: total damage against the current room
            string total = $"{card.TotalDamage(living)} {card.DamageType}";
            var size = _ctx.Font.MeasureString(total) * 0.38f;
            batch.DrawString(_ctx.Font, total,
                new Vector2(rect.Right - 26 - size.X, rect.Bottom - 30 - size.Y),
                Color.Gold, 0f, Vector2.Zero, 0.38f, SpriteEffects.None, 0f);
        }
    }
}
