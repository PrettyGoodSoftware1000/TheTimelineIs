using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Iso;

public partial class IsoLevelScreen
{
    // ---------------- a yes/no question ----------------

    /// <summary>
    /// A question the level asks and waits for an answer to. Null when there
    /// is nothing being asked.
    ///
    /// There is one at a time and it is always the same shape, so it does not
    /// need a stack or a queue: the only thing that asks is a card you are
    /// part way through, and you cannot be part way through two.
    /// </summary>
    private string? _askText;

    private Action? _askYes;

    private static readonly Rectangle AskPanel = new(1120, 820, 1600, 520);

    private static Rectangle AskButton(bool yes) =>
        new(AskPanel.X + (yes ? 160 : AskPanel.Width - 160 - 560), AskPanel.Bottom - 230, 560, 160);

    private bool Asking => _askText != null;

    /// <summary>Puts a question up. The action runs if the answer is yes.</summary>
    private void Ask(string text, Action onYes)
    {
        _askText = text;
        _askYes = onYes;
    }

    private void CloseAsk()
    {
        _askText = null;
        _askYes = null;
    }

    /// <summary>
    /// Returns true while a question is up, so Update stops there and nothing
    /// underneath sees the click that answers it.
    /// </summary>
    private bool UpdateAsk(InputState input)
    {
        if (!Asking) return false;
        // Escape is "no": backing out of a question leaves things as they were
        if (input.Cancel) { CloseAsk(); return true; }
        return true;
    }

    private void DrawAsk(SpriteBatch batch)
    {
        if (_askText == null) return;

        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height), Color.Black * 0.6f);
        Ui.FillRect(batch, _ctx.Pixel, AskPanel, new Color(18, 18, 26, 245));
        Ui.DrawWrappedCentered(batch, _ctx.Font, _askText,
            new Rectangle(AskPanel.X + 80, AskPanel.Y + 90, AskPanel.Width - 160, 0),
            Color.White, 0.40f);

        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, AskButton(true),
                _ctx.Strings.Get("iso_yes"), _tap))
        {
            var yes = _askYes;
            _tap = null;
            CloseAsk();
            yes?.Invoke();
            return;
        }
        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, AskButton(false),
                _ctx.Strings.Get("iso_no"), _tap))
        {
            _tap = null;
            CloseAsk();
        }
    }

    /// <summary>
    /// Lets go of a channelled card without firing it. The points it cost are
    /// gone — it was paid for on the turn it was started — but the caster can
    /// move and act again.
    /// </summary>
    private void DropChannel(CharacterInstance who)
    {
        if (!who.IsChannelling) return;
        string was = who.ChannellingCard;
        ClearChannel(who);
        who.MovePoints = who.MoveMax;
        _hand = HandOf(ActiveMover ?? who);
        _overlayKey = null;
        Log(_ctx.Strings.Format("iso_channel_dropped",
            ("name", who.Name), ("card", Caps(was))));
    }
}
