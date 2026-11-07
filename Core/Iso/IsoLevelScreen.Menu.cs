using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Iso;

public partial class IsoLevelScreen
{
    // ---------------- the Escape menu ----------------

    /// <summary>
    /// The pause menu. Escape opens and closes it; while it is up the level
    /// below answers nothing, so a click meant for a menu button cannot also
    /// land on the board.
    ///
    /// It is where the things you do to a MISSION live, rather than in the
    /// mission. Recording used to be a button sitting across the top of the
    /// screen for the whole fight, which is a lot of room for something
    /// pressed once.
    /// </summary>
    private bool _menu;

    private const int MenuW = 1200, MenuTop = 240, MenuStep = 220, MenuBtnH = 170, MenuPad = 90;

    /// <summary>Sized to the buttons actually shown, so a hidden one leaves no hole.</summary>
    private Rectangle MenuPanelFor(int buttons)
    {
        int h = MenuTop + buttons * MenuStep - (MenuStep - MenuBtnH) + MenuPad;
        return new Rectangle((VirtualViewport.Width - MenuW) / 2,
            (VirtualViewport.Height - h) / 2, MenuW, h);
    }

    private static Rectangle MenuButton(Rectangle panel, int i) =>
        new(panel.X + 100, panel.Y + MenuTop + i * MenuStep, panel.Width - 200, MenuBtnH);

    /// <summary>
    /// Escape, and the frame it is pressed on. Returns true when the menu has
    /// taken the frame, so Update stops there.
    /// </summary>
    private bool UpdateMenu(InputState input)
    {
        // The steal picker owns Escape while it is up — it is a question
        // waiting for an answer, and "take nothing" is one of the answers.
        if (_mode == Mode.StealPick) return false;

        if (input.Cancel)
        {
            // a card being aimed is the nearer thing to back out of, so
            // Escape cancels that first and only then reaches the menu
            if (!_menu && (_selectedCard != null || _targets.Count > 0))
            {
                CancelCard();
                return true;
            }
            _menu = !_menu;
            return true;
        }
        return _menu;
    }

    private void DrawMenu(SpriteBatch batch)
    {
        if (!_menu) return;

        bool canRecord = _ctx.ReplayStore.CanKeep && !_replayMode;
        var panel = MenuPanelFor(canRecord ? 2 : 1);

        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height), Color.Black * 0.6f);
        Ui.FillRect(batch, _ctx.Pixel, panel, new Color(18, 18, 26, 245));
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("iso_menu_title"),
            new Rectangle(panel.X, panel.Y + 70, panel.Width, 120), Color.White, 0.52f);

        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, MenuButton(panel, 0),
                _ctx.Strings.Get("iso_menu_resume"), _tap))
        {
            _menu = false;
            _tap = null;
            return;
        }

        // Recording lives here now. It is hidden rather than greyed where the
        // platform cannot keep a replay at all — the browser — because an
        // explanation nobody can act on is just clutter.
        if (canRecord)
        {
            bool justSaved = _replaySavedTimer > 0;
            string label = justSaved ? "replay_done" : _recording ? "replay_stop" : "replay_start";
            Color? tint = justSaved ? new Color(24, 86, 34, 235)
                : _recording ? new Color(110, 30, 30, 235) : null;
            if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, MenuButton(panel, 1),
                    _ctx.Strings.Get(label), _tap, tint))
            {
                _tap = null;
                if (_recording) SaveReplay("asked for"); else StartRecording();
            }
        }
    }
}
