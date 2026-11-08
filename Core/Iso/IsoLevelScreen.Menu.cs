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

    /// <summary>The Settings page, which the menu opens into and Escape backs out of.</summary>
    private bool _settings;

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
            // Escape backs out one layer at a time: the card you are aiming,
            // then the Settings page, then the menu itself.
            if (!_menu && (_selectedCard != null || _targets.Count > 0))
            {
                CancelCard();
                return true;
            }
            if (_menu && _settings) { _settings = false; return true; }
            _menu = !_menu;
            _settings = false;
            return true;
        }
        return _menu;
    }

    private void DrawMenu(SpriteBatch batch)
    {
        if (!_menu) return;
        if (_settings) { DrawSettings(batch); return; }

        bool canRecord = _ctx.ReplayStore.CanKeep && !_replayMode;
        var panel = MenuPanelFor(canRecord ? 3 : 2);

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

        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, MenuButton(panel, 1),
                _ctx.Strings.Get("iso_menu_settings"), _tap))
        {
            _settings = true;
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
            if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, MenuButton(panel, 2),
                    _ctx.Strings.Get(label), _tap, tint))
            {
                _tap = null;
                if (_recording) SaveReplay("asked for"); else StartRecording();
            }
        }
    }

    // ---------------- Settings ----------------

    private const int SetRowH = 150, SetArrowW = 110, SetValueW = 560;

    /// <summary>
    /// One row of the Settings page: a name on the left, and on the right a
    /// value with an arrow either side of it.
    ///
    /// Arrows rather than a dropdown because there are three of them and they
    /// are in an order — you step along it and watch the board behind change.
    /// </summary>
    private void DrawSettings(SpriteBatch batch)
    {
        var panel = MenuPanelFor(2);

        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height), Color.Black * 0.6f);
        Ui.FillRect(batch, _ctx.Pixel, panel, new Color(18, 18, 26, 245));
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("iso_menu_settings"),
            new Rectangle(panel.X, panel.Y + 70, panel.Width, 120), Color.White, 0.52f);

        var row = new Rectangle(panel.X + 60, panel.Y + MenuTop, panel.Width - 120, SetRowH);

        // the name gets exactly the room the value block does not, so a long
        // one cannot run under the left arrow
        int valueX = row.Right - SetValueW;
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("set_bars"),
            new Rectangle(row.X, row.Y, valueX - row.X - 24, row.Height), Color.White, 0.32f);
        var left = new Rectangle(valueX, row.Y + 20, SetArrowW, row.Height - 40);
        var right = new Rectangle(row.Right - SetArrowW, row.Y + 20, SetArrowW, row.Height - 40);
        var value = new Rectangle(left.Right, row.Y, right.X - left.Right, row.Height);

        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, left, "<", _tap))
        {
            _ctx.Settings.StepBars(-1);
            _tap = null;
        }
        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, right, ">", _tap))
        {
            _ctx.Settings.StepBars(1);
            _tap = null;
        }
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get(_ctx.Settings.BarsKey),
            value, Color.Gold, 0.34f);

        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, MenuButton(panel, 1),
                _ctx.Strings.Get("iso_menu_back"), _tap))
        {
            _settings = false;
            _tap = null;
        }
    }
}
