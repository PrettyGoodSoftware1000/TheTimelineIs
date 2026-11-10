using System;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Pixel;

/// <summary>
/// Maps world pixels onto the window without ever changing their shape.
///
/// The rest of the game authors at 3840x2160 and squashes that onto whatever
/// window it gets, so a source pixel lands on 0.7 of a screen pixel and the
/// hardware smears the difference. That is fine for painted art and fatal for
/// pixel art.
///
/// Here the transform is a multiply off a fixed ladder and a whole-number
/// scroll. One art pixel is exactly Zoom screen pixels square — the same
/// everywhere on screen, in every asset. Zooming changes how many pixels you
/// can see, never what a pixel looks like.
/// </summary>
public class PixelCamera
{
    /// <summary>
    /// The zoom levels, in order, in screen pixels per art pixel.
    ///
    /// Whole numbers but for the FIRST. A half step means one art pixel
    /// covers one and a half screen pixels, which the hardware has to smear —
    /// the exact thing the rest of this class exists to avoid. There is one
    /// of them and it is the bottom rung, because seeing more of the board at
    /// once was worth it; everything above it is exact.
    /// </summary>
    private static readonly float[] Steps = { 1.5f, 2f, 3f, 4f, 5f, 6f, 7f, 8f };

    /// <summary>Where a level opens: far enough out to see a fight, still exact.</summary>
    private const int OpeningStep = 1;

    public static float MinZoom => Steps[0];
    public static float MaxZoom => Steps[^1];

    private int _step = OpeningStep;

    /// <summary>Screen pixels per art pixel.</summary>
    public float Zoom { get; private set; } = Steps[OpeningStep];

    /// <summary>
    /// Where the window's top-left corner sits in world pixels. Kept whole:
    /// half a pixel of scroll would put every sprite on a half pixel, which is
    /// the one thing this whole class exists to prevent.
    /// </summary>
    public Point Corner { get; private set; }

    public Matrix Matrix =>
        Matrix.CreateTranslation(-Corner.X, -Corner.Y, 0f) *
        Matrix.CreateScale(Zoom, Zoom, 1f);

    public void Scroll(Point byPixels) => Corner += byPixels;

    public void ScrollTo(Point world) => Corner = world;

    /// <summary>
    /// Centres the view on a world point, rounded to a whole pixel.
    /// </summary>
    public void CentreOn(Point world, int windowW, int windowH) =>
        Corner = new Point(
            world.X - (int)(windowW / (2 * Zoom)),
            world.Y - (int)(windowH / (2 * Zoom)));

    /// <summary>
    /// Zooms a step in or out, keeping the world point under the cursor under
    /// the cursor. Without that a wheel zoom drifts, because the corner stays
    /// put while everything around it grows.
    /// </summary>
    public void ZoomBy(int steps, Point atScreen)
    {
        if (steps == 0) return;
        var before = ToWorld(atScreen);
        _step = Math.Clamp(_step + steps, 0, Steps.Length - 1);
        Zoom = Steps[_step];
        var after = ToWorld(atScreen);
        Corner += new Point(before.X - after.X, before.Y - after.Y);
    }

    /// <summary>A point on the window, in world pixels.</summary>
    public Point ToWorld(Point screen) =>
        new(Corner.X + (int)(screen.X / Zoom), Corner.Y + (int)(screen.Y / Zoom));

    /// <summary>A point in the world, on the window.</summary>
    public Point ToScreen(Point world) =>
        new((int)((world.X - Corner.X) * Zoom), (int)((world.Y - Corner.Y) * Zoom));

    /// <summary>The zoom as the ~ menu shows it: "2" or "1.5", never "2.0".</summary>
    public string ZoomLabel => Zoom.ToString("0.##",
        System.Globalization.CultureInfo.InvariantCulture);
}
