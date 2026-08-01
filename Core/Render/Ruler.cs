using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace TheTimelineIs.Core.Render;

/// <summary>
/// F12 debug ruler. The screen is 12 units tall (1 unit = 180 virtual px =
/// 1/12 of the letterboxed height at any window size); the same unit runs
/// across the top (a 16:9 screen is 21.33 units wide). Deliberately exempt
/// from every Config.txt scale so it stays a fixed yardstick.
/// </summary>
public static class Ruler
{
    public const int UnitPx = VirtualViewport.Height / 12; // 180

    public static void Draw(SpriteBatch batch, GameContext ctx)
    {
        var pixel = ctx.Pixel;
        Ui.FillRect(batch, pixel, new Rectangle(0, 0, 130, VirtualViewport.Height), Color.Black * 0.55f);
        Ui.FillRect(batch, pixel, new Rectangle(0, 0, VirtualViewport.Width, 130), Color.Black * 0.55f);

        for (int i = 0; i <= 12; i++)
        {
            int y = System.Math.Min(i * UnitPx, VirtualViewport.Height - 4);
            Ui.FillRect(batch, pixel, new Rectangle(0, y, 100, 4), Color.Yellow);
            if (i < 12)
                batch.DrawString(ctx.Font, i.ToString(), new Vector2(24, y + 14),
                    Color.Yellow, 0f, Vector2.Zero, 0.35f, SpriteEffects.None, 0f);
        }
        for (int i = 0; i <= VirtualViewport.Width / UnitPx; i++)
        {
            int x = i * UnitPx;
            Ui.FillRect(batch, pixel, new Rectangle(x, 0, 4, 100), Color.Yellow);
            if (i > 0) // 0 is already labeled on the vertical ruler
                batch.DrawString(ctx.Font, i.ToString(), new Vector2(x + 12, 20),
                    Color.Yellow, 0f, Vector2.Zero, 0.35f, SpriteEffects.None, 0f);
        }
        // half-unit minor ticks
        for (int y = UnitPx / 2; y < VirtualViewport.Height; y += UnitPx)
            Ui.FillRect(batch, pixel, new Rectangle(0, y, 50, 3), Color.Yellow * 0.6f);
        for (int x = UnitPx / 2; x < VirtualViewport.Width; x += UnitPx)
            Ui.FillRect(batch, pixel, new Rectangle(x, 0, 3, 50), Color.Yellow * 0.6f);
    }
}
