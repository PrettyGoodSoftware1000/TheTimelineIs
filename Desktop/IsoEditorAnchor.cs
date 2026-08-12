using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Iso;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Desktop;

/// <summary>
/// The Anchor tool: the half of the editor that decides where a piece of ground
/// art sits on its square, and how big it is drawn.
///
/// It shows the selected .png over a crosshair — one vertical and one
/// horizontal line, one pixel wide — with the real 360x180 grid diamond drawn
/// where the two cross. Drag the art to move it under the crosshair; the pixel
/// left under the crossing point is the anchor. Scroll to size the art until
/// its top face matches the diamond.
///
/// The preview is drawn with exactly the arithmetic the game uses, so what is
/// lined up here is what appears in the level. Both numbers are written back
/// into Blocks.txt in the source tree, and only the Anchor and Scale lines of
/// the piece being edited are touched — every comment and every other piece in
/// that file is left exactly as it was.
/// </summary>
public partial class IsoEditorScreen
{
    private bool _anchoring;
    private GroundPiece? _anchorPiece;
    private Vector2 _anchorPoint;      // in IMAGE pixels: what lands on the crosshair
    private float _anchorScale = 1f;
    private Point? _anchorDragFrom;    // pointer position the current drag started at

    /// <summary>Where the crosshair sits, and therefore the square's top-face centre.</summary>
    private static readonly Vector2 AnchorOrigin =
        new(VirtualViewport.Width / 2f, VirtualViewport.Height / 2f + 120);

    private const float MinScale = 0.02f, MaxScale = 4f;

    private void ToggleAnchorTool()
    {
        if (_anchoring) { _anchoring = false; Status("anchor tool closed"); return; }

        if (SelectedPiece is not GroundPiece piece)
        {
            Status("no ground piece selected to anchor");
            return;
        }
        _anchoring = true;
        _tool = Tool.Block;
        _openMenu = null;
        _anchorPiece = piece;
        _anchorPoint = new Vector2(piece.Anchor.X, piece.Anchor.Y);
        _anchorScale = piece.Scale;
        _anchorDragFrom = null;
        Status($"anchoring {piece.File} — drag to place, scroll to size, Enter saves, Esc cancels");
    }

    /// <summary>
    /// Runs instead of the level editing while the tool is open. Returns true
    /// when it has taken the frame, so the caller stops there.
    /// </summary>
    private bool UpdateAnchorTool(InputState input)
    {
        if (!_anchoring || _anchorPiece == null) return false;

        // the toolbar keeps working, so Anchor can be clicked again to close
        if (input.Tap is Point tap && ToolbarBand.Contains(tap)) return false;

        if (input.Cancel) { _anchoring = false; Status("anchor cancelled, nothing written"); return true; }
        if (input.Confirm) { SaveAnchor(); return true; }

        // scroll sizes the art about the crosshair, so the anchor stays put
        if (input.ScrollDelta != 0)
            _anchorScale = Math.Clamp(_anchorScale * MathF.Pow(1.08f, input.ScrollDelta),
                MinScale, MaxScale);

        // drag moves the ART under a fixed crosshair, so the anchor is whatever
        // pixel ends up beneath it. Screen movement converts back into image
        // pixels through the current scale.
        if (input.PointerHeld)
        {
            if (_anchorDragFrom is Point from)
            {
                var delta = (_pointer - from).ToVector2();
                if (_anchorScale > 0f) _anchorPoint -= delta / _anchorScale;
            }
            _anchorDragFrom = _pointer;
        }
        else _anchorDragFrom = null;

        return true;
    }

    /// <summary>
    /// Writes Anchor and Scale back into Blocks.txt, rewriting only those two
    /// lines under the piece being edited and inserting them when they are
    /// missing. Everything else in the file survives untouched.
    /// </summary>
    private void SaveAnchor()
    {
        if (_anchorPiece is not GroundPiece piece) return;

        string path = Path.Combine(SourceContentDir, "Images", "Blocks", "Blocks.txt");
        if (!File.Exists(path))
        {
            Status($"cannot find {path}");
            return;
        }

        var anchor = new Point((int)MathF.Round(_anchorPoint.X), (int)MathF.Round(_anchorPoint.Y));
        string anchorLine = $"Anchor: {anchor.X}, {anchor.Y}";
        string scaleLine = $"Scale: {(_anchorScale * 100f).ToString("0.###", CultureInfo.InvariantCulture)}";

        var lines = File.ReadAllLines(path).ToList();
        bool inPiece = false, wroteAnchor = false, wroteScale = false;
        int lastPieceLine = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith('#') || trimmed.Length == 0) continue;

            int colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;
            string key = trimmed[..colon].Trim().ToLowerInvariant();
            string value = trimmed[(colon + 1)..].Trim();

            if (key is "piece" or "family")
            {
                inPiece = key == "piece" &&
                          value.Equals(piece.File, StringComparison.OrdinalIgnoreCase);
                if (inPiece) lastPieceLine = i;
                continue;
            }
            if (!inPiece) continue;
            if (key == "anchor") { lines[i] = anchorLine; wroteAnchor = true; }
            else if (key == "scale") { lines[i] = scaleLine; wroteScale = true; }
        }

        if (lastPieceLine < 0)
        {
            Status($"{piece.File} is not listed in Blocks.txt — add a Piece: line for it first");
            return;
        }
        // a piece declared without them gets both lines inserted right below it
        if (!wroteScale) lines.Insert(lastPieceLine + 1, scaleLine);
        if (!wroteAnchor) lines.Insert(lastPieceLine + 1, anchorLine);

        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);

        // re-read so the level behind the tool redraws with the new numbers
        BlockCatalog.Reset();
        _anchoring = false;
        Status($"{piece.File}: anchor {anchor.X}, {anchor.Y} at {_anchorScale * 100f:0.#}% -> Blocks.txt");
    }

    /// <summary>The repo's Content folder, so a save lands in the source tree.</summary>
    private static string SourceContentDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "TheTimelineIs.sln")))
                    return Path.Combine(dir.FullName, "Content");
                dir = dir.Parent;
            }
            return Path.Combine(AppContext.BaseDirectory, "Content");
        }
    }

    private void DrawAnchorTool(SpriteBatch batch)
    {
        if (!_anchoring || _anchorPiece is not GroundPiece piece) return;

        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height),
            new Color(16, 16, 22, 245));

        var tex = _ctx.Assets.LoadTexture(piece.Path);

        // exactly the arithmetic BlockCatalog.Draw uses, so this preview is the
        // game's own placement rather than an approximation of it
        var rect = new Rectangle(
            (int)MathF.Round(AnchorOrigin.X - _anchorPoint.X * _anchorScale),
            (int)MathF.Round(AnchorOrigin.Y - _anchorPoint.Y * _anchorScale),
            Math.Max(1, (int)MathF.Round(tex.Width * _anchorScale)),
            Math.Max(1, (int)MathF.Round(tex.Height * _anchorScale)));
        batch.Draw(tex, rect, Color.White);

        // the square this piece will stand on, at its true size
        var diamond = new Rectangle(
            (int)(AnchorOrigin.X - IsoMath.TileW / 2f), (int)(AnchorOrigin.Y - IsoMath.TileH / 2f),
            IsoMath.TileW, IsoMath.TileH);
        batch.Draw(_ctx.Assets.LoadTexture("Content/Images/Blocks/OverlayTop.png"), diamond,
            new Color(120, 200, 255) * 0.20f);
        batch.Draw(_ctx.Assets.LoadTexture("Content/Images/Blocks/OverlayEdge.png"), diamond,
            new Color(120, 200, 255) * 0.95f);

        // the crosshair: one pixel each way, right across the screen, so the
        // exact anchor is readable against any art
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle((int)AnchorOrigin.X, 0, 1, VirtualViewport.Height), Color.OrangeRed);
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, (int)AnchorOrigin.Y, VirtualViewport.Width, 1), Color.OrangeRed);

        var anchor = new Point((int)MathF.Round(_anchorPoint.X), (int)MathF.Round(_anchorPoint.Y));
        Ui.DrawTextCentered(batch, _ctx.Font,
            $"{piece.File}   {tex.Width}x{tex.Height}px",
            new Rectangle(0, 300, VirtualViewport.Width, 70), Color.White, 0.42f);
        Ui.DrawTextCentered(batch, _ctx.Font,
            $"anchor {anchor.X}, {anchor.Y}    scale {_anchorScale * 100f:0.#}%",
            new Rectangle(0, 370, VirtualViewport.Width, 60), Color.Gold, 0.36f);
        Ui.DrawTextCentered(batch, _ctx.Font,
            "drag the art to move it under the crosshair  ·  scroll to size it against the diamond",
            new Rectangle(0, 430, VirtualViewport.Width, 50), Color.White * 0.75f, 0.26f);
        Ui.DrawTextCentered(batch, _ctx.Font,
            "Enter writes it to Blocks.txt  ·  Esc cancels",
            new Rectangle(0, 476, VirtualViewport.Width, 50), Color.White * 0.75f, 0.26f);
    }
}
