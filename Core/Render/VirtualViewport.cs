using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace TheTimelineIs.Core.Render;

/// <summary>
/// The game is authored at a fixed 3840x2160. This maps that design space
/// onto whatever the real backbuffer is, letterboxing as needed, so the
/// same coordinates work on a 720p window and a 4:3 tablet alike.
/// </summary>
public class VirtualViewport
{
    public const int Width = 3840;
    public const int Height = 2160;

    public float Scale { get; private set; } = 1f;
    public Point Offset { get; private set; }
    public Matrix Matrix => Matrix.CreateScale(Scale);

    private Viewport _letterboxed;

    public void Update(GraphicsDevice device)
    {
        int bbWidth = device.PresentationParameters.BackBufferWidth;
        int bbHeight = device.PresentationParameters.BackBufferHeight;

        Scale = System.Math.Min(bbWidth / (float)Width, bbHeight / (float)Height);
        int w = (int)(Width * Scale);
        int h = (int)(Height * Scale);
        Offset = new Point((bbWidth - w) / 2, (bbHeight - h) / 2);
        _letterboxed = new Viewport(Offset.X, Offset.Y, w, h);
    }

    /// <summary>Restrict rendering to the letterboxed region. Call before drawing the scene.</summary>
    public void Apply(GraphicsDevice device) => device.Viewport = _letterboxed;

    public Point ScreenToVirtual(Point screen) =>
        new((int)((screen.X - Offset.X) / Scale), (int)((screen.Y - Offset.Y) / Scale));

    public Vector2 ScreenToVirtual(Vector2 screenDelta) => screenDelta / Scale;
}
