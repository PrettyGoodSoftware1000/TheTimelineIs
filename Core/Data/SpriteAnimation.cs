using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// One sprite sheet cut into frames, plus how fast to play it and how big to
/// draw it. The sheet is an ordinary PNG loaded through <see cref="AssetLoader"/>;
/// the grid comes from a companion .txt sitting beside it with the same name,
/// which is exactly what spritetool writes:
///
///   Sheet: WerewitchSpell1.png
///   FrameWidth: 666
///   FrameHeight: 375
///   Columns: 3
///   Rows: 16
///   Frames: 48
///
/// Frame N sits at x = OffsetX + (N % Columns) * FrameWidth, y likewise, so the
/// frames run left to right then top to bottom.
///
/// Four keys are optional and default sensibly, so a plain spritetool sheet
/// needs none of them:
///
///   OffsetX / OffsetY  where the grid starts inside the image. Sheets built by
///                      spritetool start at 0,0 and fill the whole PNG. Sheets
///                      that came from somewhere else often sit in the middle of
///                      a much larger canvas with dead transparent margins all
///                      round, and then the grid has to be told where it begins.
///                      "spritetool detect" works these out and writes them.
///   FPS                frames per second, default 30. Playback is a constant
///                      rate: an animation is as long as its frame count makes
///                      it, and nothing stretches it to fit a casting time.
///   Scale              percent, default 100. 100% draws a frame exactly as
///                      tall as the character it replaces, whatever the art's
///                      own resolution, so undersized frames are scaled up.
///                      Raise it when the character sits small inside its cell.
/// </summary>
public class SpriteAnimation
{
    public Texture2D Sheet = null!;
    public int FrameWidth, FrameHeight;
    public int OffsetX, OffsetY;
    public int Columns = 1;
    public int FrameCount;
    public float Fps = DefaultFps;
    /// <summary>1f = draw a frame at the height of the sprite it stands in for.</summary>
    public float Scale = 1f;

    public const float DefaultFps = 30f;

    /// <summary>How long one pass takes, in seconds.</summary>
    public float Duration => Fps <= 0f ? 0f : FrameCount / Fps;

    /// <summary>Which frame is showing this many seconds in; the last one once it's over.</summary>
    public int FrameAt(float seconds)
    {
        if (FrameCount <= 1 || Fps <= 0f) return 0;
        return Math.Clamp((int)(seconds * Fps), 0, FrameCount - 1);
    }

    /// <summary>The patch of the sheet holding frame N, counting from 0.</summary>
    public Rectangle SourceRect(int frame)
    {
        int n = Math.Clamp(frame, 0, Math.Max(0, FrameCount - 1));
        int cols = Math.Max(1, Columns);
        return new Rectangle(
            OffsetX + n % cols * FrameWidth,
            OffsetY + n / cols * FrameHeight,
            FrameWidth, FrameHeight);
    }

    /// <summary>
    /// Where to draw a frame so it stands in for a sprite.
    ///
    /// Sprites are drawn tall — around 9:16 — while animation frames are wide,
    /// around 16:9, to leave room around the character for the effect. So the
    /// two are matched by HEIGHT and centred on each other: the frame is scaled
    /// until it is as tall as the sprite it replaces (up or down, whatever the
    /// art's own resolution happens to be), keeps its own aspect ratio, and is
    /// then centred on the sprite's centre in both directions. The extra width
    /// falls evenly either side instead of squashing the character inside it.
    ///
    /// Scale nudges that: 100% is exactly the sprite's height, and raising it
    /// grows the frame about its centre, for art where the character sits small
    /// inside its cell.
    /// </summary>
    public Rectangle RectFor(Rectangle spriteRect)
    {
        int h = Math.Max(1, (int)(spriteRect.Height * Scale));
        int w = FrameHeight <= 0 ? h : Math.Max(1, (int)(h * FrameWidth / (float)FrameHeight));
        return new Rectangle(
            spriteRect.Center.X - w / 2,
            spriteRect.Center.Y - h / 2,
            w, h);
    }

    // ---------------- loading ----------------

    private static readonly Dictionary<string, SpriteAnimation?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every key the .txt may carry besides "Sheet:", all of them numbers.</summary>
    private static readonly HashSet<string> NumericKeys = new(StringComparer.Ordinal)
    {
        "framewidth", "frame width", "frameheight", "frame height",
        "offsetx", "offset x", "offsety", "offset y",
        "columns", "rows", "frames", "fps", "scale",
    };

    /// <summary>
    /// Loads the sheet at <paramref name="path"/> and the .txt beside it,
    /// caching both. Returns null — after one diagnostic — if either is missing
    /// or the numbers don't describe a usable grid, so a character with broken
    /// animation art simply keeps its static sprite.
    /// </summary>
    public static SpriteAnimation? Load(AssetLoader assets, string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        var loaded = Read(assets, path);
        Cache[path] = loaded;
        return loaded;
    }

    /// <summary>The companion file for a sheet: Spell.png -> Spell.txt.</summary>
    public static string MetaPathFor(string sheetPath) =>
        System.IO.Path.ChangeExtension(sheetPath, ".txt");

    private static SpriteAnimation? Read(AssetLoader assets, string path)
    {
        var diag = Diagnostics.Current;
        string meta = MetaPathFor(path);

        if (!AssetLoader.Exists(path))
        {
            diag.Error(path, 0, "animation sheet is missing, so the static sprite is used instead");
            return null;
        }
        if (!AssetLoader.Exists(meta))
        {
            diag.Error(meta, 0,
                $"'{System.IO.Path.GetFileName(path)}' has no .txt beside it saying how it is cut " +
                "into frames — run 'spritetool detect' on it to write one");
            return null;
        }

        var anim = new SpriteAnimation();
        int rows = 0, frames = 0;

        foreach (var (lineNo, raw) in AssetLoader.ReadNumbered(meta, meta))
        {
            string line = TextUtil.Clean(raw);
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                diag.Error(meta, lineNo, $"unrecognized line '{line}' — expected 'Key: value'");
                continue;
            }
            string key = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();

            // "Sheet:" names the PNG this file describes; we already have it
            if (key == "sheet") continue;

            // an unrecognized key is reported as unrecognized, rather than as
            // an unrecognized key that also failed to be a number
            if (!NumericKeys.Contains(key))
            {
                diag.Warn(meta, lineNo, $"unknown line '{line}' ignored");
                continue;
            }
            if (!TryNumber(value, out float number))
            {
                diag.Error(meta, lineNo, $"'{key}' needs a number, got '{value}'");
                continue;
            }
            switch (key)
            {
                case "framewidth" or "frame width": anim.FrameWidth = (int)number; break;
                case "frameheight" or "frame height": anim.FrameHeight = (int)number; break;
                case "offsetx" or "offset x": anim.OffsetX = (int)number; break;
                case "offsety" or "offset y": anim.OffsetY = (int)number; break;
                case "columns": anim.Columns = (int)number; break;
                case "rows": rows = (int)number; break;
                case "frames": frames = (int)number; break;
                case "fps": anim.Fps = number; break;
                // a percentage, like every other scale in this project
                case "scale": anim.Scale = number / 100f; break;
            }
        }

        if (anim.FrameWidth <= 0 || anim.FrameHeight <= 0)
        {
            diag.Error(meta, 0, "FrameWidth and FrameHeight are required and must be above zero");
            return null;
        }
        if (anim.Columns <= 0) anim.Columns = 1;
        if (anim.Fps <= 0f) anim.Fps = DefaultFps;
        if (anim.Scale <= 0f) anim.Scale = 1f;

        anim.Sheet = assets.LoadTexture(path, out bool found);
        if (!found) return null;   // LoadTexture already reported it

        // Frames is the authority; Rows only fills in when it is missing
        anim.FrameCount = frames > 0 ? frames
            : rows > 0 ? rows * anim.Columns
            : anim.Columns;

        // a grid that runs off the edge means the numbers don't match the art,
        // and slicing it would hand back blank or torn frames
        var last = anim.SourceRect(anim.FrameCount - 1);
        if (last.Right > anim.Sheet.Width || last.Bottom > anim.Sheet.Height)
        {
            diag.Error(meta, 0,
                $"the grid runs off the sheet: {anim.FrameCount} frames of " +
                $"{anim.FrameWidth}x{anim.FrameHeight} in {anim.Columns} column(s) from " +
                $"({anim.OffsetX},{anim.OffsetY}) needs {last.Right}x{last.Bottom} px, but " +
                $"{System.IO.Path.GetFileName(path)} is {anim.Sheet.Width}x{anim.Sheet.Height}");
            return null;
        }

        return anim;
    }

    private static bool TryNumber(string value, out float number) =>
        float.TryParse(value.TrimEnd('%', ' '), NumberStyles.Float,
            CultureInfo.InvariantCulture, out number);
}
