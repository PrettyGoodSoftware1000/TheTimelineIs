using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// The ~ menu: the handful of things worth doing to a level while looking at
/// it, kept away from the game's own rules.
///
/// - Win Level / Die! end the mission either way, for testing what comes next.
/// - Scale Stuff resizes any character or decoration on the fly.
/// </summary>
public partial class IsoLevelScreen
{
    private bool _devMenu;
    private bool _scaleMenu;

    /// <summary>Which row of the scale list is being typed into, or -1.</summary>
    private int _scaleRow = -1;
    private string _scaleBuf = "";

    /// <summary>Every scalable thing, with the percentage currently typed against it.</summary>
    private readonly List<(string Name, string Value)> _scaleRows = new();

    private static readonly Rectangle DevPanel = new(1180, 700, 1480, 760);

    private static Rectangle DevButton(int i) =>
        new(DevPanel.X + 90, DevPanel.Y + 160 + i * 190, DevPanel.Width - 180, 150);

    /// <summary>~ opens and closes it. Nothing else on screen answers while it is up.</summary>
    private void ToggleDevMenu()
    {
        _devMenu = !_devMenu;
        _scaleMenu = false;
        _scaleRow = -1;
    }

    /// <summary>
    /// Runs the menu instead of the game. Returns true when it swallowed the
    /// frame, so the level below does not also act on the same click.
    /// </summary>
    private bool UpdateDevMenu(InputState input)
    {
        if (input.ToggleDevMenu) { ToggleDevMenu(); return true; }
        if (!_devMenu) return false;
        if (_scaleMenu) UpdateScaleMenu(input);
        return true;
    }

    private void DrawDevMenu(SpriteBatch batch)
    {
        if (!_devMenu) return;
        if (_scaleMenu) { DrawScaleMenu(batch); return; }

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
        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, DevButton(2), _ctx.Strings.Get("dev_scale"), _tap))
            OpenScaleMenu();

        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("dev_close"),
            new Rectangle(DevPanel.X, DevPanel.Bottom - 90, DevPanel.Width, 70),
            Color.White * 0.7f, 0.32f);
        _tap = null;
    }

    // ---------------- scale stuff ----------------

    /// <summary>
    /// Builds the list: every class, every summon, every enemy, every
    /// decoration, plus the two catch-all lines. Ground is left out — tiles are
    /// sized by the grid itself, and scaling one would just break the floor.
    /// </summary>
    private void OpenScaleMenu()
    {
        _scaleMenu = true;
        _scaleRow = -1;
        _scaleRows.Clear();

        foreach (string name in ScalableNames())
            _scaleRows.Add((name, Percent(_ctx.Config.RawScale(name))));
    }

    /// <summary>Everything the menu offers a box for, in the order it lists them.</summary>
    private IEnumerable<string> ScalableNames()
    {
        yield return "Global";
        yield return "Cast";
        foreach (string n in _ctx.Classes.ClassNames)
        {
            yield return n;
            // a shapeshifter's shapes are drawn from different art and rarely
            // want the same size, so each gets its own box
            foreach (var form in _ctx.Classes.Get(n)!.Forms)
                yield return $"{n} {form.Name}";
        }
        foreach (string n in _ctx.Enemies.EnemyNames) yield return n;
        foreach (string d in BlockCatalog.Decorations) yield return DecorationScaleName(d);
    }

    /// <summary>"Tree1.png" -> "Tree1", which is what a Config.txt line would say.</summary>
    private static string DecorationScaleName(string file)
    {
        int dot = file.LastIndexOf('.');
        return dot > 0 ? file[..dot] : file;
    }

    /// <summary>0 shows as "0", meaning "no line of its own"; anything else as a whole percent.</summary>
    private static string Percent(float scale) =>
        scale <= 0f ? "0" : Math.Round(scale * 100f).ToString(CultureInfo.InvariantCulture);

    private static Rectangle ScaleRowRect(int i, int scroll)
    {
        int y = 220 + (i - scroll) * 88;
        return new Rectangle(300, y, 3240, 76);
    }

    /// <summary>How many rows fit on screen at once.</summary>
    private const int ScaleRowsShown = 18;
    private int _scaleScroll;

    private void UpdateScaleMenu(InputState input)
    {
        if (input.ScrollDelta != 0)
            _scaleScroll = Math.Clamp(_scaleScroll - input.ScrollDelta, 0,
                Math.Max(0, _scaleRows.Count - ScaleRowsShown));

        // Enter takes everything typed, applies it, writes it out and closes
        if (input.Submit)
        {
            CommitScales();
            _scaleMenu = false;
            _devMenu = false;
            _scaleRow = -1;
            return;
        }
        if (input.Cancel) { _scaleMenu = false; _scaleRow = -1; return; }

        if (_tap is Point press)
        {
            _tap = null;
            _scaleRow = -1;
            for (int i = _scaleScroll; i < _scaleRows.Count && i < _scaleScroll + ScaleRowsShown; i++)
                if (ScaleRowRect(i, _scaleScroll).Contains(press))
                {
                    _scaleRow = i;
                    _scaleBuf = _scaleRows[i].Value;
                    break;
                }
        }

        if (_scaleRow < 0) return;

        // digits only: a percentage is a number, and letting anything else in
        // would only make a line Config.txt cannot read back
        foreach (char ch in input.TypedChars)
            if (char.IsDigit(ch) && _scaleBuf.Length < 4) _scaleBuf += ch;
        if (input.Backspace && _scaleBuf.Length > 0) _scaleBuf = _scaleBuf[..^1];
        _scaleRows[_scaleRow] = (_scaleRows[_scaleRow].Name, _scaleBuf);

        // applied as it is typed, so the board underneath resizes live
        ApplyScales();
    }

    /// <summary>Pushes what is typed into the live config, without saving.</summary>
    private void ApplyScales()
    {
        foreach (var (name, value) in _scaleRows)
            _ctx.Config.SetScale(name,
                int.TryParse(value, out int pct) ? pct / 100f : 0f);
    }

    /// <summary>Applies the values and writes them back into Config.txt.</summary>
    private void CommitScales()
    {
        ApplyScales();
        if (_ctx.DevWriter is null)
        {
            Toast(_ctx.Strings.Get("dev_scale_unsaved"));
            return;
        }
        string? where = _ctx.DevWriter.Write(GameConfig.Path, _ctx.Config.Serialize());
        Toast(where == null
            ? _ctx.Strings.Get("dev_scale_unsaved")
            : _ctx.Strings.Get("dev_scale_saved"));
    }

    private void DrawScaleMenu(SpriteBatch batch)
    {
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height),
            new Color(10, 10, 16, 246));
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("dev_scale_title"),
            new Rectangle(0, 90, VirtualViewport.Width, 90), Color.Yellow, 0.5f);

        for (int i = _scaleScroll; i < _scaleRows.Count && i < _scaleScroll + ScaleRowsShown; i++)
        {
            var row = ScaleRowRect(i, _scaleScroll);
            bool editing = i == _scaleRow;
            Ui.FillRect(batch, _ctx.Pixel, row,
                editing ? new Color(40, 40, 70, 255) : new Color(22, 22, 30, 255));

            batch.DrawString(_ctx.Font, _scaleRows[i].Name,
                new Vector2(row.X + 24, row.Y + 16), Color.White,
                0f, Vector2.Zero, 0.34f, SpriteEffects.None, 0f);

            var box = new Rectangle(row.Right - 420, row.Y + 8, 380, 60);
            Ui.FillRect(batch, _ctx.Pixel, box, new Color(0, 0, 0, 200));
            string shown = (editing ? _scaleBuf + "_" : _scaleRows[i].Value) + " %";
            Ui.DrawTextCentered(batch, _ctx.Font, shown, box,
                _scaleRows[i].Value == "0" ? Color.Gray : Color.LightGreen, 0.34f);
        }

        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("dev_scale_hint"),
            new Rectangle(0, VirtualViewport.Height - 120, VirtualViewport.Width, 70),
            Color.White * 0.75f, 0.3f);
    }
}
