using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;

namespace TheTimelineIs.Core.Render;

/// <summary>
/// Draws a room's scenery: the background sits 4 feet higher than the screen,
/// and the ground fills the strip that leaves behind at the bottom — the band
/// the characters stand on. Shared by the room and battle screens so the two
/// can't drift apart.
/// </summary>
public static class Backdrop
{
    /// <summary>How far the background is lifted, in feet.</summary>
    public const int RaiseFeet = 4;

    /// <summary>720 virtual px at 180 px per foot.</summary>
    public static int RaisePx => RaiseFeet * Ruler.UnitPx;

    public const string GroundPath = "Content/Images/Grounds/Ground1.png";

    /// <summary>The strip the raised background leaves bare: 3840 x 720.</summary>
    public static Rectangle GroundRect => new(
        0, VirtualViewport.Height - RaisePx, VirtualViewport.Width, RaisePx);

    public static void Draw(SpriteBatch batch, GameContext ctx, Texture2D background, Color tint)
    {
        var screen = new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height);

        Rectangle placed;
        if (background == ctx.Pixel)
        {
            // no art for this room: flat colour where the background would be
            placed = new Rectangle(screen.X, screen.Y - RaisePx, screen.Width, screen.Height);
            Ui.FillRect(batch, ctx.Pixel, placed, new Color(20, 30, 20));
        }
        else
        {
            var size = AssetLoader.DisplaySize(background, AssetKind.Background)
                * ctx.Config.GlobalScale;
            placed = Ui.FitCentered(size, screen);
            placed.Y -= RaisePx;
            batch.Draw(background, placed, tint);
        }

        // ground covers everything from the background's bottom edge downward,
        // so it still closes the gap if the background is scaled or off-ratio
        int top = System.Math.Min(placed.Bottom, VirtualViewport.Height - RaisePx);
        if (top >= VirtualViewport.Height) return;
        var gap = new Rectangle(0, top, VirtualViewport.Width, VirtualViewport.Height - top);
        batch.Draw(ctx.Assets.LoadTexture(GroundPath), gap, Color.White);
    }
}
