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
/// horizontal line, one pixel wide — with the real grid diamond drawn where the
/// two cross. Drag the art to move it under the crosshair; the pixel left under
/// the crossing point is the anchor. Size it against the diamond by scrolling,
/// or by typing a percentage.
///
/// Everything here is drawn at <see cref="PreviewZoom"/>x, art and diamond
/// alike, so a few pixels of misalignment are visible. Because both are
/// magnified by the same factor what is lined up here is still exactly what
/// appears in the level; the zoom never reaches the numbers that are saved.
/// </summary>
public partial class IsoEditorScreen
{
    private bool _anchoring;
    private GroundPiece? _anchorPiece;
    private Vector2 _anchorPoint;      // in IMAGE pixels: what lands on the crosshair
    private float _anchorScale = 1f;
    private Point? _anchorDragFrom;    // pointer position the current drag started at
    private string? _scaleBuffer;      // non-null while a scale is being typed

    /// <summary>Where the crosshair sits, and therefore the square's top-face centre.</summary>
    private static readonly Vector2 AnchorOrigin =
        new(VirtualViewport.Width / 2f, VirtualViewport.Height / 2f + 60);

    /// <summary>Preview magnification. Affects nothing that is written to disk.</summary>
    private const float PreviewZoom = 2f;

    private const float MinScale = 0.02f, MaxScale = 4f;

    /// <summary>One notch of the wheel, as a multiplier. Ctrl takes a third of it.</summary>
    private const float ScaleStep = 1.08f;
    private const float FineFraction = 1f / 3f;

    private void ToggleAnchorTool()
    {
        if (_anchoring) { _anchoring = false; _scaleBuffer = null; Status("anchor tool closed"); return; }

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
        _scaleBuffer = null;
        Status($"anchoring {piece.File} — drag to place, scroll to size (Ctrl = fine), " +
               "type a number to set the scale, Enter saves, Esc cancels");
    }

    /// <summary>
    /// Runs instead of the level editing while the tool is open. Returns true
    /// when it has taken the frame, so the caller stops there.
    /// </summary>
    private bool UpdateAnchorTool(InputState input)
    {
        if (!_anchoring || _anchorPiece == null) return false;

        // the toolbar keeps working, so Anchor and Reload can still be clicked
        if (input.Tap is Point tap && ToolbarBand.Contains(tap)) return false;

        // Typing a scale takes the keyboard while it is happening: Enter then
        // commits the number rather than saving, and Esc abandons the number
        // rather than the tool. The buffer is on screen throughout, so which
        // one Enter means is never a guess.
        if (TypingScale(input)) return true;

        if (input.Cancel) { _anchoring = false; Status("anchor cancelled, nothing written"); return true; }
        if (input.Confirm) { SaveAnchor(); return true; }

        // scroll sizes the art about the crosshair, so the anchor stays put.
        // Ctrl takes a third of a notch for the last pixel or two.
        if (input.ScrollDelta != 0)
        {
            float step = input.CtrlHeld ? MathF.Pow(ScaleStep, FineFraction) : ScaleStep;
            _anchorScale = Math.Clamp(_anchorScale * MathF.Pow(step, input.ScrollDelta),
                MinScale, MaxScale);
        }

        // drag moves the ART under a fixed crosshair, so the anchor is whatever
        // pixel ends up beneath it. Screen movement converts back into image
        // pixels through the scale AND the preview zoom, so dragging tracks the
        // cursor exactly however far the preview is magnified.
        if (input.PointerHeld)
        {
            if (_anchorDragFrom is Point from)
            {
                var delta = (_pointer - from).ToVector2();
                float perPixel = _anchorScale * PreviewZoom;
                if (perPixel > 0f) _anchorPoint -= delta / perPixel;
            }
            _anchorDragFrom = _pointer;
        }
        else _anchorDragFrom = null;

        return true;
    }

    /// <summary>
    /// Scale entry by keyboard. Typing a digit or a dot starts it; Enter takes
    /// the number, Esc drops it. Returns true while it owns the keyboard.
    /// </summary>
    private bool TypingScale(InputState input)
    {
        if (_scaleBuffer == null)
        {
            string start = new(input.TypedChars.Where(c => char.IsDigit(c) || c == '.').ToArray());
            if (start.Length == 0) return false;
            _scaleBuffer = start;
            return true;
        }

        _scaleBuffer += new string(input.TypedChars.Where(c => char.IsDigit(c) || c == '.').ToArray());
        if (input.Backspace && _scaleBuffer.Length > 0) _scaleBuffer = _scaleBuffer[..^1];
        if (input.Cancel) { _scaleBuffer = null; Status("scale unchanged"); return true; }

        if (input.Submit || input.Confirm)
        {
            if (float.TryParse(_scaleBuffer, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float pct) && pct > 0f)
            {
                _anchorScale = Math.Clamp(pct / 100f, MinScale, MaxScale);
                Status($"scale {_anchorScale * 100f:0.##}% — Enter again to save");
            }
            else Status($"'{_scaleBuffer}' is not a percentage");
            _scaleBuffer = null;
        }
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

        // Every square on the level redraws from the catalog each frame, so
        // reloading it is all that is needed for the blocks already placed to
        // take the new anchor and scale.
        ReloadGroundArt();
        _anchoring = false;
        _scaleBuffer = null;
        Status($"{piece.File}: anchor {anchor.X}, {anchor.Y} at {_anchorScale * 100f:0.##}% " +
               "— saved, and every placed block redrawn");
    }

    /// <summary>
    /// Re-reads Blocks.txt, so hand edits and anything the Anchor tool just
    /// wrote show up without restarting.
    ///
    /// The copy step is the awkward part. Content is read through
    /// TitleContainer, which resolves against the built output next to the
    /// executable, while the editor writes to the source tree — so a plain
    /// re-read would faithfully reload the stale copy that was there at build
    /// time. Copying source over output first is what makes the reload mean
    /// what it says.
    /// </summary>
    private void ReloadGroundArt()
    {
        const string relative = "Images/Blocks/Blocks.txt";
        string source = Path.Combine(SourceContentDir, relative.Replace('/', Path.DirectorySeparatorChar));
        string output = Path.Combine(AppContext.BaseDirectory, "Content",
            relative.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (File.Exists(source) && !string.Equals(
                    Path.GetFullPath(source), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(source, output, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Status($"could not refresh the built copy of Blocks.txt: {ex.Message}");
        }
        BlockCatalog.Reset();
        RecountProblems();
    }

    /// <summary>The Reload button: pick up Blocks.txt as it stands on disk.</summary>
    private void ReloadFromDisk()
    {
        ReloadGroundArt();
        Status($"reloaded Blocks.txt — {BlockCatalog.Pieces.Count} piece(s) in " +
               $"{BlockCatalog.Families.Count} famil{(BlockCatalog.Families.Count == 1 ? "y" : "ies")}");
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

        // BlockCatalog.Draw's arithmetic with the preview zoom folded in. Art
        // and diamond take the same factor, so lining them up here lines them
        // up in the level.
        float shown = _anchorScale * PreviewZoom;
        var rect = new Rectangle(
            (int)MathF.Round(AnchorOrigin.X - _anchorPoint.X * shown),
            (int)MathF.Round(AnchorOrigin.Y - _anchorPoint.Y * shown),
            Math.Max(1, (int)MathF.Round(tex.Width * shown)),
            Math.Max(1, (int)MathF.Round(tex.Height * shown)));
        batch.Draw(tex, rect, Color.White);

        // the square this piece will stand on, magnified to match
        int dw = (int)(IsoMath.TileW * PreviewZoom), dh = (int)(IsoMath.TileH * PreviewZoom);
        var diamond = new Rectangle(
            (int)(AnchorOrigin.X - dw / 2f), (int)(AnchorOrigin.Y - dh / 2f), dw, dh);
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
            $"{piece.File}   {tex.Width}x{tex.Height}px   shown at {PreviewZoom:0.#}x",
            new Rectangle(0, 286, VirtualViewport.Width, 70), Color.White, 0.40f);
        Ui.DrawTextCentered(batch, _ctx.Font,
            _scaleBuffer != null
                ? $"anchor {anchor.X}, {anchor.Y}    scale {_scaleBuffer}_ %"
                : $"anchor {anchor.X}, {anchor.Y}    scale {_anchorScale * 100f:0.##}%",
            new Rectangle(0, 352, VirtualViewport.Width, 60),
            _scaleBuffer != null ? Color.Cyan : Color.Gold, 0.36f);
        Ui.DrawTextCentered(batch, _ctx.Font,
            _scaleBuffer != null
                ? "Enter takes the number  ·  Esc leaves the scale as it was"
                : "drag to move it under the crosshair  ·  scroll to size it, Ctrl to fine tune  " +
                  "·  type a number to set the scale",
            new Rectangle(0, 410, VirtualViewport.Width, 50), Color.White * 0.75f, 0.26f);
        if (_scaleBuffer == null)
            Ui.DrawTextCentered(batch, _ctx.Font,
                "Enter writes it to Blocks.txt and redraws every placed block  ·  Esc cancels",
                new Rectangle(0, 456, VirtualViewport.Width, 50), Color.White * 0.75f, 0.26f);
    }
}
