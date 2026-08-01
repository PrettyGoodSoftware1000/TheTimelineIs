using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace TheTimelineIs.Core.Render;

/// <summary>
/// F12 debug ruler. Origin (0,0) is the BOTTOM-LEFT of the screen; feet run
/// up the left edge and right along the bottom. One foot = 180 virtual px =
/// 1/12 of the screen height at any window size, so the screen is 12 feet
/// tall and (at 16:9) 21.3 feet wide. Deliberately exempt from every
/// Config.txt scale so it stays a fixed yardstick.
/// </summary>
public static class Ruler
{
    public const int UnitPx = VirtualViewport.Height / 12; // 180 px = 1 foot
    public const int FeetTall = 12;

    private static readonly Color Major = Color.Yellow;
    private static readonly Color Half = new(0, 200, 200); // teal half-foot ticks

    /// <summary>Feet above the bottom of the screen -> virtual y (which grows downward).</summary>
    public static int FeetToY(float feet) => VirtualViewport.Height - (int)(feet * UnitPx);

    /// <summary>Feet from the left edge -> virtual x.</summary>
    public static int FeetToX(float feet) => (int)(feet * UnitPx);

    public static void Draw(SpriteBatch batch, GameContext ctx)
    {
        var pixel = ctx.Pixel;
        Ui.FillRect(batch, pixel, new Rectangle(0, 0, 210, VirtualViewport.Height), Color.Black * 0.55f);
        Ui.FillRect(batch, pixel,
            new Rectangle(0, VirtualViewport.Height - 130, VirtualViewport.Width, 130), Color.Black * 0.55f);

        // half-foot ticks first, so whole-foot ticks draw over them
        for (float f = 0.5f; f < FeetTall; f += 1f)
        {
            int y = FeetToY(f);
            Ui.FillRect(batch, pixel, new Rectangle(0, y - 1, 60, 3), Half);
        }
        for (float f = 0.5f; f < VirtualViewport.Width / (float)UnitPx; f += 1f)
        {
            int x = FeetToX(f);
            Ui.FillRect(batch, pixel, new Rectangle(x - 1, VirtualViewport.Height - 60, 3, 60), Half);
        }

        // whole feet up the left edge
        for (int f = 0; f <= FeetTall; f++)
        {
            int y = Mathf(FeetToY(f), 4);
            Ui.FillRect(batch, pixel, new Rectangle(0, y - 2, 110, 4), Major);
            batch.DrawString(ctx.Font, $"{f} feet", new Vector2(26, y - 60),
                Major, 0f, Vector2.Zero, 0.32f, SpriteEffects.None, 0f);
        }

        // whole feet across the bottom
        int wide = VirtualViewport.Width / UnitPx;
        for (int f = 1; f <= wide; f++)
        {
            int x = FeetToX(f);
            Ui.FillRect(batch, pixel,
                new Rectangle(x - 2, VirtualViewport.Height - 110, 4, 110), Major);
            batch.DrawString(ctx.Font, $"{f} feet",
                new Vector2(x + 12, VirtualViewport.Height - 105),
                Major, 0f, Vector2.Zero, 0.32f, SpriteEffects.None, 0f);
        }
    }

    /// <summary>Keeps the topmost/bottommost tick fully on screen.</summary>
    private static int Mathf(int y, int thickness) =>
        System.Math.Clamp(y, thickness, VirtualViewport.Height - thickness);
}
