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

    private static readonly Rectangle DevPanel = new(1180, 700, 1480, 760);

    private static Rectangle DevButton(int i) =>
        new(DevPanel.X + 90, DevPanel.Y + 160 + i * 190, DevPanel.Width - 180, 150);

    /// <summary>The frame rates the menu steps through. 12 is where it opens.</summary>
    private static readonly int[] FrameRates = { 4, 6, 8, 10, 12, 15, 18, 24, 30 };

    /// <summary>~ opens and closes it. Nothing else on screen answers while it is up.</summary>
    private void ToggleDevMenu() => _devMenu = !_devMenu;

    /// <summary>
    /// Runs the menu instead of the game. Returns true when it swallowed the
    /// frame, so the level below does not also act on the same click.
    /// </summary>
    private bool UpdateDevMenu(InputState input)
    {
        if (input.ToggleDevMenu) { ToggleDevMenu(); return true; }
        return _devMenu;
    }

    private void DrawDevMenu(SpriteBatch batch)
    {
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

        // one button, stepping round the list: fewer things to hit, and every
        // press is a change you can see on the board straight away
        string fps = _ctx.Strings.Format("dev_fps", ("fps", ((int)DirectionalSprite.Fps).ToString()));
        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, DevButton(2), fps, _tap))
        {
            int at = Array.IndexOf(FrameRates, (int)DirectionalSprite.Fps);
            DirectionalSprite.Fps = FrameRates[(at + 1) % FrameRates.Length];
        }

        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("dev_close"),
            new Rectangle(DevPanel.X, DevPanel.Bottom - 90, DevPanel.Width, 70),
            Color.White * 0.7f, 0.32f);
        _tap = null;
    }
}
