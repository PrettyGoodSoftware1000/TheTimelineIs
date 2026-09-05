using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// Anchor Art: nudging a character's picture on its square while looking at it.
///
/// Art arrives with the figure wherever the artist drew it, and on a 64-wide
/// square being a couple of pixels off centre is plain to see. Rather than
/// re-export, the offset is tuned here by eye and written to Anchors.txt.
///
/// SIDEWAYS by default. Height comes from the lowest solid pixel of the
/// picture, which is a character's feet whatever canvas they were drawn on, and
/// that has been right every time so far. Vertical is a box to tick for the
/// exceptions rather than a second number everybody has to get right.
///
/// It runs over the LEVEL, not the map, because the whole point is watching the
/// character move against ground you can see.
/// </summary>
public partial class IsoLevelScreen
{
    private bool _anchorMenu;

    /// <summary>Which of the cast is being nudged.</summary>
    private int _anchorWho;

    private static readonly Rectangle AnchorPanel = new(60, 1180, 1500, 800);

    private static Rectangle AnchorRow(int i) =>
        new(AnchorPanel.X + 40, AnchorPanel.Y + 150 + i * 96, AnchorPanel.Width - 80, 84);

    /// <summary>Everybody on the board worth nudging, one row each.</summary>
    private List<CharacterInstance> AnchorCast() =>
        Everyone.Where(c => c.Alive)
            .GroupBy(c => ArtAnchors.KeyFor(c.Name, c.Art), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void OpenAnchorMenu()
    {
        _anchorMenu = true;
        _devMenu = false;
        _anchorWho = 0;
    }

    /// <summary>
    /// Arrows nudge, V toggles vertical, Tab picks the next character, Enter
    /// saves, Esc leaves it alone. Returns true while it owns the frame.
    /// </summary>
    private bool UpdateAnchorMenu(InputState input)
    {
        if (!_anchorMenu) return false;
        var cast = AnchorCast();
        if (cast.Count == 0) { _anchorMenu = false; return true; }
        _anchorWho = Math.Clamp(_anchorWho, 0, cast.Count - 1);

        if (input.Cancel) { _anchorMenu = false; return true; }
        if (input.Submit) { SaveAnchors(); _anchorMenu = false; return true; }

        if (_tap is Point press)
        {
            _tap = null;
            for (int i = 0; i < cast.Count; i++)
                if (AnchorRow(i).Contains(press)) { _anchorWho = i; break; }
            // the tick box at the right of the row toggles vertical
            var box = AnchorTickBox(AnchorRow(_anchorWho));
            if (box.Contains(press)) Nudge(cast[_anchorWho], 0, 0, flipVertical: true);
        }

        if (input.SelectAll) _anchorWho = (_anchorWho + 1) % cast.Count;   // Tab
        foreach (char ch in input.TypedChars)
            if (ch is 'v' or 'V') Nudge(cast[_anchorWho], 0, 0, flipVertical: true);

        // Arrows move the picture a pixel at a time. Up and down only do
        // anything once vertical is ticked, which is the whole of the
        // difference between the two axes.
        var by = new Point(
            (input.Nudge.X != 0 ? Math.Sign(input.Nudge.X) : 0),
            (input.Nudge.Y != 0 ? Math.Sign(input.Nudge.Y) : 0));
        if (by != Point.Zero) Nudge(cast[_anchorWho], by.X, by.Y);
        return true;
    }

    private static Rectangle AnchorTickBox(Rectangle row) =>
        new(row.Right - 420, row.Y + 14, 56, 56);

    private void Nudge(CharacterInstance who, int dx, int dy, bool flipVertical = false)
    {
        var now = _ctx.Anchors.For(who.Name, who.Art);
        bool vertical = flipVertical ? !now.Vertical : now.Vertical;
        _ctx.Anchors.Set(who.Name, who.Art,
            new ArtAnchor(now.X + dx, vertical ? now.Y - dy : now.Y, vertical));
    }

    private void SaveAnchors()
    {
        if (_ctx.DevWriter is null) { Toast(_ctx.Strings.Get("dev_anchor_unsaved")); return; }
        string? where = _ctx.DevWriter.Write(ArtAnchors.Path, _ctx.Anchors.Serialize());
        Toast(_ctx.Strings.Get(where == null ? "dev_anchor_unsaved" : "dev_anchor_saved"));
    }

    private void DrawAnchorMenu(SpriteBatch batch)
    {
        if (!_anchorMenu) return;
        var cast = AnchorCast();
        if (cast.Count == 0) return;

        // a panel down one side only: the board stays visible, because seeing
        // the nudge land is the entire point of doing this here
        Ui.FillRect(batch, _ctx.Pixel, AnchorPanel, new Color(14, 14, 20, 235));
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("dev_anchor_title"),
            new Rectangle(AnchorPanel.X, AnchorPanel.Y + 30, AnchorPanel.Width, 80),
            Color.Yellow, 0.42f);

        for (int i = 0; i < cast.Count && i < 6; i++)
        {
            var row = AnchorRow(i);
            var who = cast[i];
            var a = _ctx.Anchors.For(who.Name, who.Art);
            bool picked = i == _anchorWho;
            Ui.FillRect(batch, _ctx.Pixel, row,
                picked ? new Color(44, 44, 76) : new Color(22, 22, 30));

            batch.DrawString(_ctx.Font, ArtAnchors.KeyFor(who.Name, who.Art),
                new Vector2(row.X + 22, row.Y + 20), picked ? Color.White : Color.Gray,
                0f, Vector2.Zero, 0.32f, SpriteEffects.None, 0f);

            var box = AnchorTickBox(row);
            Ui.FillRect(batch, _ctx.Pixel, box, new Color(0, 0, 0, 200));
            if (a.Vertical)
                Ui.DrawTextCentered(batch, _ctx.Font, "x", box, Color.LightGreen, 0.4f);
            batch.DrawString(_ctx.Font, _ctx.Strings.Get("dev_anchor_vertical"),
                new Vector2(box.Right + 12, row.Y + 22), Color.White * 0.8f,
                0f, Vector2.Zero, 0.26f, SpriteEffects.None, 0f);

            string shown = a.Vertical ? $"{a.X:+#;-#;0}, {a.Y:+#;-#;0}" : $"{a.X:+#;-#;0}";
            Ui.DrawTextCentered(batch, _ctx.Font, shown,
                new Rectangle(row.Right - 190, row.Y, 170, row.Height),
                a == ArtAnchors.None ? Color.Gray : Color.LightGreen, 0.34f);
        }

        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("dev_anchor_hint"),
            new Rectangle(AnchorPanel.X, AnchorPanel.Bottom - 90, AnchorPanel.Width, 70),
            Color.White * 0.75f, 0.26f);
        _tap = null;
    }
}
