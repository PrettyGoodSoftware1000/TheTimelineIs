using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace TheTimelineIs.Core.Render;

/// <summary>
/// Minimal immediate-mode UI: filled rects, wrapped text, and tappable
/// buttons. Buttons are sized generously (touch rule: nothing smaller than
/// ~44 physical points, which at 4K design scale means big rectangles).
/// </summary>
public static class Ui
{
    public static void FillRect(SpriteBatch batch, Texture2D pixel, Rectangle rect, Color color) =>
        batch.Draw(pixel, rect, color);

    /// <summary>
    /// Largest rectangle of the given aspect that fits inside bounds, centered.
    /// Art is never stretched — letterboxed within its slot instead.
    /// </summary>
    public static Rectangle FitCentered(Vector2 size, Rectangle bounds)
    {
        float scale = System.Math.Min(bounds.Width / size.X, bounds.Height / size.Y);
        int w = (int)(size.X * scale);
        int h = (int)(size.Y * scale);
        return new Rectangle(
            bounds.X + (bounds.Width - w) / 2,
            bounds.Y + (bounds.Height - h) / 2, w, h);
    }

    public static void DrawTextCentered(SpriteBatch batch, SpriteFont font, string text,
        Rectangle bounds, Color color, float scale)
    {
        var size = font.MeasureString(text) * scale;
        var pos = new Vector2(
            bounds.X + (bounds.Width - size.X) / 2f,
            bounds.Y + (bounds.Height - size.Y) / 2f);
        batch.DrawString(font, text, pos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    /// <summary>Draw a button; returns true if the given tap landed on it.</summary>
    public static bool Button(SpriteBatch batch, Texture2D pixel, SpriteFont font,
        Rectangle rect, string label, Point? tap, Color? background = null)
    {
        FillRect(batch, pixel, rect, background ?? new Color(30, 30, 30, 220));
        FillRect(batch, pixel, new Rectangle(rect.X, rect.Y, rect.Width, 4), Color.White * 0.5f);
        FillRect(batch, pixel, new Rectangle(rect.X, rect.Bottom - 4, rect.Width, 4), Color.White * 0.5f);
        FillRect(batch, pixel, new Rectangle(rect.X, rect.Y, 4, rect.Height), Color.White * 0.5f);
        FillRect(batch, pixel, new Rectangle(rect.Right - 4, rect.Y, 4, rect.Height), Color.White * 0.5f);
        DrawTextCentered(batch, font, label, rect, Color.White, 0.5f);
        return tap.HasValue && rect.Contains(tap.Value);
    }

    public static string Wrap(SpriteFont font, string text, float maxWidth, float scale)
    {
        var result = new StringBuilder();
        var line = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (font.MeasureString(candidate).X * scale > maxWidth && line.Length > 0)
            {
                result.Append(line).Append('\n'); // '\n' only: SpriteFont has no glyph for '\r'
                line.Clear();
                line.Append(word);
            }
            else
            {
                line.Clear();
                line.Append(candidate);
            }
        }
        if (line.Length > 0) result.Append(line);
        return result.ToString();
    }
}
