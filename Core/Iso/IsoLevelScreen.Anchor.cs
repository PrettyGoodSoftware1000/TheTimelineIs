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
/// Anchor Art: nudging a picture on its square while looking at it.
///
/// Art arrives with the figure wherever the artist drew it, and on a square one
/// tile wide being a couple of pixels off centre is plain to see. Rather than
/// re-export, the offset is tuned here by eye and written to Anchors.txt.
///
/// Both arrows move it ACROSS THE GROUND — left and right is east and west, up
/// and down is north and south. Nothing is lifted: something nudged south is
/// drawn with the band of ground it has moved onto, so it never sinks under
/// tiles painted after it. Height still comes from the lowest solid pixel.
///
/// Objects are in the list too — doors, trash cans, the Vessel's gate — because
/// a portal drawn a few pixels off its square looks exactly as wrong as a
/// character does. Six to a page; Tab turns the page.
///
/// It runs over the LEVEL, not the map, because the whole point is watching the
/// nudge land against ground you can see.
/// </summary>
public partial class IsoLevelScreen
{
    private bool _anchorMenu;

    /// <summary>Which row is being nudged, counted through the whole list.</summary>
    private int _anchorWho;

    /// <summary>One thing that can be nudged: a row, and a key in Anchors.txt.</summary>
    private readonly record struct AnchorTarget(string Key, bool IsObject);

    private const int AnchorRows = 6;

    private static readonly Rectangle AnchorPanel = new(60, 1100, 1500, 880);

    private static Rectangle AnchorRow(int i) =>
        new(AnchorPanel.X + 40, AnchorPanel.Y + 150 + i * 96, AnchorPanel.Width - 80, 84);

    /// <summary>
    /// Everything on the board worth nudging: the cast first, then the objects
    /// standing on it. One row per PICTURE, not per body — two imps share a
    /// nudge because they share their art.
    /// </summary>
    private List<AnchorTarget> AnchorList()
    {
        var rows = Everyone.Where(c => c.Alive)
            .Select(c => ArtAnchors.KeyFor(c.Name, c.Art))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => new AnchorTarget(k, false))
            .ToList();

        var objects = _level.Decorations.Select(d => d.File).ToList();
        if (_gates.Count > 0) objects.Add(GateArtKey);
        rows.AddRange(objects
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => new AnchorTarget(k, true)));
        return rows;
    }

    private void OpenAnchorMenu()
    {
        _anchorMenu = true;
        _devMenu = false;
        _anchorWho = 0;
    }

    /// <summary>
    /// Arrows nudge, Tab turns the page, Enter saves, Esc leaves it alone.
    /// Returns true while it owns the frame.
    /// </summary>
    private bool UpdateAnchorMenu(InputState input)
    {
        if (!_anchorMenu) return false;
        var rows = AnchorList();
        if (rows.Count == 0) { _anchorMenu = false; return true; }
        _anchorWho = Math.Clamp(_anchorWho, 0, rows.Count - 1);

        if (input.Cancel) { _anchorMenu = false; return true; }
        if (input.Submit) { SaveAnchors(); _anchorMenu = false; return true; }

        // Tab turns the PAGE, not the row: there are more pictures on a board
        // than fit down one panel, and stepping one row at a time to reach the
        // last of them was the slowest way to do it. Clicking picks the row.
        if (input.SelectAll)
        {
            int page = (_anchorWho / AnchorRows + 1) % Pages(rows.Count);
            _anchorWho = page * AnchorRows;
        }

        if (_tap is Point press)
        {
            _tap = null;
            int first = _anchorWho / AnchorRows * AnchorRows;
            for (int i = 0; i < AnchorRows && first + i < rows.Count; i++)
                if (AnchorRow(i).Contains(press)) { _anchorWho = first + i; break; }
        }

        // A pixel a press, across the ground: left and right is east and west,
        // up and down is north and south.
        var by = new Point(Math.Sign(input.Nudge.X), Math.Sign(input.Nudge.Y));
        if (by != Point.Zero) Nudge(rows[_anchorWho].Key, by.X, by.Y);
        return true;
    }

    private static int Pages(int count) => Math.Max(1, (count + AnchorRows - 1) / AnchorRows);

    private void Nudge(string key, int dx, int dy)
    {
        var now = _ctx.Anchors.For(key);
        _ctx.Anchors.Set(key, new ArtAnchor(now.X + dx, now.Y - dy));
    }

    private void SaveAnchors()
    {
        if (_ctx.SourceWriter is null) { Toast(_ctx.Strings.Get("dev_anchor_unsaved")); return; }
        string? where = _ctx.SourceWriter.Write(ArtAnchors.Path, _ctx.Anchors.Serialize());
        Toast(_ctx.Strings.Get(where == null ? "dev_anchor_unsaved" : "dev_anchor_saved"));
    }

    private void DrawAnchorMenu(SpriteBatch batch)
    {
        if (!_anchorMenu) return;
        var rows = AnchorList();
        if (rows.Count == 0) return;

        // a panel down one side only: the board stays visible, because seeing
        // the nudge land is the entire point of doing this here
        Ui.FillRect(batch, _ctx.Pixel, AnchorPanel, new Color(14, 14, 20, 235));
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("dev_anchor_title"),
            new Rectangle(AnchorPanel.X, AnchorPanel.Y + 30, AnchorPanel.Width, 80),
            Color.Yellow, 0.42f);

        int pages = Pages(rows.Count);
        int page = _anchorWho / AnchorRows;
        Ui.DrawTextCentered(batch, _ctx.Font,
            _ctx.Strings.Format("dev_anchor_page",
                ("page", (page + 1).ToString()), ("of", pages.ToString())),
            new Rectangle(AnchorPanel.Right - 300, AnchorPanel.Y + 40, 260, 60),
            Color.White * 0.7f, 0.3f);

        for (int i = 0; i < AnchorRows && page * AnchorRows + i < rows.Count; i++)
        {
            var row = AnchorRow(i);
            var target = rows[page * AnchorRows + i];
            var a = _ctx.Anchors.For(target.Key);
            bool picked = page * AnchorRows + i == _anchorWho;
            Ui.FillRect(batch, _ctx.Pixel, row,
                picked ? new Color(44, 44, 76) : new Color(22, 22, 30));

            batch.DrawString(_ctx.Font, target.Key,
                new Vector2(row.X + 22, row.Y + 20), picked ? Color.White : Color.Gray,
                0f, Vector2.Zero, 0.32f, SpriteEffects.None, 0f);

            // objects are called out, because "Door.png" and a character's name
            // read the same in a list and are nudged for different reasons
            if (target.IsObject)
                batch.DrawString(_ctx.Font, _ctx.Strings.Get("dev_anchor_object"),
                    new Vector2(row.Right - 560, row.Y + 24), Color.SkyBlue * 0.8f,
                    0f, Vector2.Zero, 0.26f, SpriteEffects.None, 0f);

            string shown = a.Y == 0 ? $"{a.X:+#;-#;0}" : $"{a.X:+#;-#;0}, {a.Y:+#;-#;0}";
            Ui.DrawTextCentered(batch, _ctx.Font, shown,
                new Rectangle(row.Right - 190, row.Y, 170, row.Height),
                a == ArtAnchors.None ? Color.Gray : Color.LightGreen, 0.34f);
        }

        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("dev_anchor_hint"),
            new Rectangle(AnchorPanel.X, AnchorPanel.Bottom - 150, AnchorPanel.Width, 60),
            Color.White * 0.75f, 0.26f);
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("dev_anchor_keys"),
            new Rectangle(AnchorPanel.X, AnchorPanel.Bottom - 90, AnchorPanel.Width, 60),
            Color.White * 0.6f, 0.24f);
        _tap = null;
    }
}
