using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Screens;

/// <summary>
/// Blocks on content problems until the player acknowledges them. Errors are
/// listed first, then warnings; the full set always goes to the log file, and
/// the popup shows as many as fit with a pointer to the log for the rest.
/// </summary>
public class ErrorScreen : IScreen
{
    private const int MaxShown = 11;

    private readonly GameContext _ctx;
    private readonly List<Diagnostic> _items;
    private readonly IScreen _next;
    private readonly string _logPath;
    private Point? _tap;

    private static readonly Rectangle ContinueRect = new(1620, 1860, 600, 170);

    public ErrorScreen(GameContext ctx, IEnumerable<Diagnostic> items, string logPath, IScreen next)
    {
        _ctx = ctx;
        _items = items.ToList();
        _logPath = logPath;
        _next = next;
    }

    public void Update(InputState input, float dt)
    {
        _tap = input.Tap;
        if (input.Confirm)
            _ctx.SwitchTo(_next);
    }

    public void Draw(SpriteBatch batch)
    {
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height), new Color(18, 10, 10));

        int errors = _items.Count(i => i.Severity == Severity.Error);
        int warnings = _items.Count - errors;
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("error_title"),
            new Rectangle(0, 90, VirtualViewport.Width, 130), new Color(255, 90, 90), 0.8f);
        Ui.DrawTextCentered(batch, _ctx.Font,
            _ctx.Strings.Format("error_counts",
                ("errors", errors.ToString()), ("warnings", warnings.ToString())),
            new Rectangle(0, 230, VirtualViewport.Width, 90), Color.White * 0.8f, 0.42f);

        var panel = new Rectangle(160, 350, VirtualViewport.Width - 320, 1400);
        Ui.FillRect(batch, _ctx.Pixel, panel, new Color(0, 0, 0, 190));

        int y = panel.Y + 40;
        foreach (var item in _items.Take(MaxShown))
        {
            var color = item.Severity == Severity.Error ? new Color(255, 120, 120) : new Color(255, 215, 130);
            string where = item.Line > 0 ? $"{item.Source} line {item.Line}" : item.Source;
            batch.DrawString(_ctx.Font, where, new Vector2(panel.X + 40, y),
                color, 0f, Vector2.Zero, 0.30f, SpriteEffects.None, 0f);
            string body = Ui.Wrap(_ctx.Font, item.Message, panel.Width - 120, 0.30f);
            batch.DrawString(_ctx.Font, body, new Vector2(panel.X + 70, y + 42),
                Color.White * 0.92f, 0f, Vector2.Zero, 0.30f, SpriteEffects.None, 0f);
            y += 46 + 40 * (body.Count(c => c == '\n') + 1);
        }

        if (_items.Count > MaxShown)
            Ui.DrawTextCentered(batch, _ctx.Font,
                _ctx.Strings.Format("error_more", ("count", (_items.Count - MaxShown).ToString())),
                new Rectangle(panel.X, panel.Bottom - 110, panel.Width, 70), Color.White * 0.7f, 0.32f);

        Ui.DrawTextCentered(batch, _ctx.Font,
            _ctx.Strings.Format("error_log", ("path", _logPath)),
            new Rectangle(0, 1780, VirtualViewport.Width, 70), Color.White * 0.65f, 0.28f);

        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, ContinueRect, _ctx.Strings.Get("error_continue"), _tap))
            _ctx.SwitchTo(_next);

        _tap = null;
    }
}
