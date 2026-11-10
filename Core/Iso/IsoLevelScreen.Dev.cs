using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Pixel;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// The ~ menu: the handful of things worth doing to a level while looking at
/// it, kept away from the game's own rules.
///
/// - Win Level / Die! end the mission either way, for testing what comes next.
/// - Frame rate speeds every animation up or down, live, so an artist can
///   see what a set of frames looks like at 8 or 12 or 24 without rebuilding.
/// </summary>
public partial class IsoLevelScreen
{
    private bool _devMenu;

    private static readonly Rectangle DevPanel = new(1180, 560, 1480, 1060);

    private static Rectangle DevButton(int i) =>
        new(DevPanel.X + 90, DevPanel.Y + 160 + i * 190, DevPanel.Width - 180, 150);

    /// <summary>
    /// The frame rate is TYPED, not stepped. Stepping round a fixed list
    /// meant the number an artist wanted was either on the list or
    /// unreachable, and getting from one end of it to the other took nine
    /// presses. Null when nothing is being typed; a string while it is, so a
    /// half-finished number is not a frame rate yet.
    /// </summary>
    private string? _fpsTyped;

    /// <summary>~ opens and closes it. Nothing else on screen answers while it is up.</summary>
    private void ToggleDevMenu() => _devMenu = !_devMenu;

    /// <summary>
    /// Runs the menu instead of the game. Returns true when it swallowed the
    /// frame, so the level below does not also act on the same click.
    /// </summary>
    private bool UpdateDevMenu(InputState input)
    {
        if (input.ToggleDevMenu)
        {
            // ~ closes the anchor page too, rather than leaving it open behind
            // a menu that has just gone away
            if (_anchorMenu) { _anchorMenu = false; return true; }
            ToggleDevMenu();
            return true;
        }
        if (UpdateAnchorMenu(input)) return true;
        if (_devMenu && _fpsTyped != null) TypeFps(input);
        return _devMenu;
    }

    /// <summary>
    /// Digits into the frame-rate box. Enter takes it, Escape throws it away,
    /// and anything that is not a digit is ignored rather than typed — the
    /// box only ever holds a number.
    /// </summary>
    private void TypeFps(InputState input)
    {
        if (input.Cancel) { _fpsTyped = null; return; }
        if (input.Submit) { CommitFps(); return; }
        if (input.Backspace && _fpsTyped!.Length > 0)
            _fpsTyped = _fpsTyped[..^1];
        foreach (char ch in input.TypedChars)
            if (char.IsDigit(ch) && _fpsTyped!.Length < 3)
                _fpsTyped += ch;
    }

    /// <summary>
    /// Takes what was typed, if it is a number worth having. An empty box or
    /// a nonsense one leaves the rate where it was rather than snapping it to
    /// some default nobody asked for.
    /// </summary>
    private void CommitFps()
    {
        if (int.TryParse(_fpsTyped, out int wanted) && wanted > 0)
            DirectionalSprite.Fps = Math.Clamp(wanted,
                DirectionalSprite.MinFps, DirectionalSprite.MaxFps);
        _fpsTyped = null;
    }

    private void DrawDevMenu(SpriteBatch batch)
    {
        DrawAnchorMenu(batch);
        if (!_devMenu) return;

        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height), Color.Black * 0.55f);
        Ui.FillRect(batch, _ctx.Pixel, DevPanel, new Color(18, 18, 26, 245));
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("dev_title"),
            new Rectangle(DevPanel.X, DevPanel.Y + 40, DevPanel.Width, 100), Color.Yellow, 0.55f);

        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, DevButton(0), _ctx.Strings.Get("dev_win"), _tap))
        {
            _devMenu = false;
            FinishMission("dev win");
            _mode = Mode.Victory;
        }
        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, DevButton(1), _ctx.Strings.Get("dev_die"), _tap))
        {
            _devMenu = false;
            foreach (var p in _party.Where(p => p.Alive)) { p.Hp = 0; p.Alive = false; }
            FinishMission("dev death");
            _ctx.SwitchTo(new Screens.DeathScreen(_ctx));
            return;
        }

        // A box you type into. While it is being typed it shows what has been
        // typed so far with a caret after it, so a half-finished number does
        // not read as the rate the game is running at.
        var fpsBox = DevButton(2);
        bool typing = _fpsTyped != null;
        string fps = typing
            ? _ctx.Strings.Format("dev_fps_typing", ("fps", _fpsTyped!))
            : _ctx.Strings.Format("dev_fps", ("fps", ((int)DirectionalSprite.Fps).ToString()));
        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, fpsBox, fps, _tap,
                typing ? new Color(40, 56, 40, 235) : null))
        {
            // clicking the box starts a fresh number; clicking it again while
            // typing takes whatever is in it
            if (typing) CommitFps(); else _fpsTyped = "";
        }
        else if (typing && _tap is Point away && !fpsBox.Contains(away))
        {
            // clicking anything else takes it too, rather than leaving a box
            // open behind whatever was pressed
            CommitFps();
        }

        if (typing)
            Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("dev_fps_hint"),
                new Rectangle(fpsBox.X, fpsBox.Bottom + 4, fpsBox.Width, 50),
                Color.White * 0.6f, 0.28f);

        // only where the anchors can be saved: not in a browser
        if (_ctx.SourceWriter != null &&
            Ui.Button(batch, _ctx.Pixel, _ctx.Font, DevButton(3), _ctx.Strings.Get("dev_anchor"), _tap))
            OpenAnchorMenu();

        // What the board is magnified by, read off the camera. Not a button:
        // the zoom is set with Ctrl and the wheel while looking at the level,
        // and this is for saying which of the eight you ended up on.
        Ui.DrawTextCentered(batch, _ctx.Font,
            _ctx.Strings.Format("dev_zoom", ("zoom", _camera.Zoom.ToString())),
            new Rectangle(DevPanel.X, DevPanel.Bottom - 170, DevPanel.Width, 70),
            Color.White * 0.85f, 0.36f);

        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("dev_close"),
            new Rectangle(DevPanel.X, DevPanel.Bottom - 90, DevPanel.Width, 70),
            Color.White * 0.7f, 0.32f);
        _tap = null;
    }
}
