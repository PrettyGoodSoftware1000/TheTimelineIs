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
    private void DrawDialogue(SpriteBatch batch)
    {
        var line = _lines![Math.Min(_lineIndex, _lines.Count - 1)];
        Ui.FillRect(batch, _ctx.Pixel, DialogueBox, new Color(0, 0, 0, 225));

        var speaker = Everyone.FirstOrDefault(c =>
            c.Name.Equals(line.Speaker, StringComparison.OrdinalIgnoreCase));
        var thumbRect = new Rectangle(DialogueBox.X + 36, DialogueBox.Y + 34, 350, 350);
        if (speaker != null)
        {
            var thumb = _ctx.Sprites.Portrait(speaker);
            batch.Draw(thumb, Ui.FitCentered(new Vector2(thumb.Width, thumb.Height), thumbRect),
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
        // a dark plate behind the toast, because white text alone disappears
        // over pale ground and this is the game's only way of answering back
        if (_toastTimer > 0 && !DialogueActive)
        {
            var size = _ctx.Font.MeasureString(_toast) * 0.42f;
            var plate = new Rectangle(64, 244, (int)size.X + 32, (int)size.Y + 24);
            Ui.FillRect(batch, _ctx.Pixel, plate, new Color(12, 12, 20, 210));
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(plate.X, plate.Y, 6, plate.Height), Color.Gold);
            batch.DrawString(_ctx.Font, _toast, new Vector2(80, 260), Color.White,
                0f, Vector2.Zero, 0.42f, SpriteEffects.None, 0f);
        }

        // Save Replay is up from the moment the level loads, not only at the
        // end: the interesting part of a mission is often over well before the
        // mission is, and a fight you want to keep is one you want to keep now.
        //
        // The button reports its own result for a few seconds. It used to say
        // nothing at all and leave the news to the toast, which prints small and
        // plain in the opposite corner of the screen — technically feedback,
        // practically invisible.
        if (ReplayButtonUp)
        {
            // Three things this button can be saying: it has just written a
            // file, it is writing things down, or it is doing nothing.
            bool justSaved = _replaySavedTimer > 0;
            string label = justSaved ? "replay_done"
                : _recording ? "replay_stop" : "replay_start";
            Color? tint = justSaved ? new Color(24, 86, 34, 235)
                : _recording ? new Color(110, 30, 30, 235) : null;

            // the press is handled in HitButton; this only paints it
            Ui.Button(batch, _ctx.Pixel, _ctx.Font, SaveReplayRect,
                _ctx.Strings.Get(label), null, tint);

            // a dot beside it while it is running, so a recording left on is
            // obvious without reading the button
            if (_recording && !justSaved)
                Ui.FillRect(batch, _ctx.Pixel,
                    new Rectangle(SaveReplayRect.X - 46, SaveReplayRect.Y + 44, 32, 32),
                    Color.Red);
        }

        if (_replayMode) { DrawReplayHud(batch); return; }

        switch (_mode)
        {
            case Mode.Explore:
                Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("iso_explore_hint"),
                    new Rectangle(0, 40, VirtualViewport.Width, 90), Color.White * 0.7f, 0.34f);
                break;
            case Mode.PlayerTurn:
            case Mode.PlayerTarget:
                DrawTurnStrip(batch);
                if (Acting is CharacterInstance me)
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

            var face = _ctx.Sprites.Portrait(who);
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
                _petControl is CharacterInstance pet && pet.Alive
                    ? _ctx.Strings.Format("iso_pet_turn",
                        ("owner", Current.Name), ("name", pet.Name))
                    : Current.Name,
                new Vector2(TurnStrip.X, TurnStrip.Bottom + 12), Color.Gold,
                0f, Vector2.Zero, 0.38f, SpriteEffects.None, 0f);
    }

    /// <summary>
    /// Action points as a row of pips: filled for what's left, hollow for what
    /// has been spent this turn. A third pip appears only when a point was
    /// carried over, which is the whole tell that rollover happened.
    /// </summary>
    /// <summary>
    /// What the current character has left to spend, written out. Ten hollow
    /// squares in a row were both wider than the screen wanted and slower to
    /// read than the number they added up to.
    /// </summary>
    private void DrawActionPoints(SpriteBatch batch, CharacterInstance who)
    {
        var label = _ctx.Strings.Format("iso_actions_left",
            ("points", who.ActionPoints.ToString()));
        Ui.DrawTextCentered(batch, _ctx.Font, label,
            new Rectangle(0, 232, VirtualViewport.Width, 60),
            who.ActionPoints > 0 ? Color.Orange : Color.Gray, 0.44f);
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

        // The key that plays each card, over its top edge. Drawn after the
        // cards so a raised hovered card cannot cover its own number.
        for (int i = 0; i < _hand.Count && i < HandKeys.Length; i++)
            Ui.DrawTextCentered(batch, _ctx.Font, HandKeys[i].ToString(),
                new Rectangle(rects[i].X, rects[i].Y - 62, rects[i].Width, 56),
                Acting is CharacterInstance who && who.ActionPoints >= _hand[i].ActionCost
                    ? Color.White : Color.White * 0.35f,
                0.42f);
    }

    /// <summary>
    /// Which key plays which card: 1 to 9, then 0 for the tenth. A hand longer
    /// than ten has no key for the rest, which is what the length check is for.
    /// </summary>
    private static readonly char[] HandKeys = { '1', '2', '3', '4', '5', '6', '7', '8', '9', '0' };

    private void DrawCard(SpriteBatch batch, Card card, Rectangle rect, bool hovered)
    {
        // Text scales with the card it is on, taken from the rect rather than
        // from `hovered`. A hovered card is CardW * HoverScale wide and so gets
        // HoverScale, exactly as before — but the steal picker narrows its
        // cards to fit a big hand on screen, and text that stayed full size on
        // a card half the width was what ran the labels into each other.
        float s = rect.Width / (float)CardW;
        // a card the holder cannot currently afford is greyed out card by card,
        // so with 1 point left the cheap ones stay lit and the dear ones dim
        bool spent = Acting == null || Acting.ActionPoints < card.ActionCost;
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
        int pad = (int)(24 * s);
        var inner = new Rectangle(rect.X + pad, rect.Y, rect.Width - pad * 2, rect.Height);

        // The name wraps and reports how tall it came out, so a long one like
        // "Beg, Borrow, but Mostly Steal" takes two lines instead of running
        // off both sides, and pushes the text under it down rather than
        // being drawn through it.
        // The name starts below the cost line rather than beside it. At the old
        // 18px both were drawn across the top of the card and "Pew Pew" ran
        // straight through "Actions 5".
        float nameTop = rect.Y + 74 * s;
        float nameHeight = Ui.DrawWrappedCentered(batch, _ctx.Font, card.Name,
            new Rectangle(inner.X, (int)nameTop, inner.Width, 0), ink, Pt(CardNamePt) * s);

        batch.DrawString(_ctx.Font,
            Ui.Wrap(_ctx.Font, card.CardText, inner.Width, Pt(CardBodyPt) * s),
            new Vector2(inner.X, nameTop + nameHeight + 22 * s), ink * 0.9f,
            0f, Vector2.Zero, Pt(CardBodyPt) * s, SpriteEffects.None, 0f);

        int hitCount = card.TargetsGround && _blastSet.Count > 0
            ? VisibleEnemies.Count(e => e.Footprint.Any(_blastSet.Contains))
            : VisibleEnemies.Count;
        // A card that rolls its damage shows the range it rolls in. Printing
        // only the top of it would read as a promise the card does not make.
        string total = card.VariableDamage
            ? $"{card.DamageMin}-{card.Damage} {card.DamageType}"
            : card.Damage <= 0 && card.Effects.Count > 0
            ? $"+{card.Effects[0].Amount} {card.Effects[0].Name}"
            : $"{card.TotalDamage(hitCount)} {card.DamageType}";
        string range = _ctx.Strings.Format("iso_card_range", ("range", card.Range.ToString()));

        // The bottom row is two labels facing each other, so each is given half
        // of the width and shrunk to fit it. Neither can reach the other however
        // long the words are — "Range 4" and "+3 Stolen" used to meet in the
        // middle of a narrow card and print over one another.
        int half = (inner.Width - (int)(16 * s)) / 2;
        float rangeScale = Ui.FitScale(_ctx.Font, range, half, Pt(CardRangePt) * s);
        float totalScale = Ui.FitScale(_ctx.Font, total, half, Pt(CardTotalPt) * s);
        float baseline = rect.Bottom - 28 * s;

        var totalSize = _ctx.Font.MeasureString(total) * totalScale;
        batch.DrawString(_ctx.Font, total,
            new Vector2(inner.Right - totalSize.X, baseline - totalSize.Y),
            spent ? Color.Gold * 0.4f : Color.Gold, 0f, Vector2.Zero, totalScale,
            SpriteEffects.None, 0f);

        var rangeSize = _ctx.Font.MeasureString(range) * rangeScale;
        batch.DrawString(_ctx.Font, range,
            new Vector2(inner.X, baseline - rangeSize.Y),
            spent ? Color.LightBlue * 0.4f : Color.LightBlue, 0f, Vector2.Zero, rangeScale,
            SpriteEffects.None, 0f);

        // The cost, top-right. It used to be one orange square per point, which
        // at ten points a turn ran off the side of the card and had to be
        // counted anyway. The number says it in less room and at a glance.
        string cost = _ctx.Strings.Format("iso_card_actions",
            ("points", card.ActionCost.ToString()));
        float costScale = Ui.FitScale(_ctx.Font, cost, inner.Width, Pt(CardRangePt) * s);
        var costSize = _ctx.Font.MeasureString(cost) * costScale;
        batch.DrawString(_ctx.Font, cost,
            new Vector2(inner.Right - costSize.X, rect.Y + 20 * s),
            spent ? Color.Orange * 0.4f : Color.Orange, 0f, Vector2.Zero, costScale,
            SpriteEffects.None, 0f);
    }
}
