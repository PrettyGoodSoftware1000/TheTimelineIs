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
/// Here the only transform is a WHOLE-number multiply and a whole-number
/// scroll. One art pixel is exactly Zoom screen pixels square — the same
/// everywhere on screen, at every zoom level, in every asset. Zooming changes
/// how many pixels you can see, never what a pixel looks like.
/// </summary>
public class PixelCamera
{
    public const int MinZoom = 1;
    public const int MaxZoom = 8;

    /// <summary>Screen pixels per art pixel. Whole numbers only, on purpose.</summary>
    public int Zoom { get; private set; } = 3;

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
            world.X - windowW / (2 * Zoom),
            world.Y - windowH / (2 * Zoom));

    /// <summary>
    /// Zooms a step in or out, keeping the world point under the cursor under
    /// the cursor. Without that a wheel zoom drifts, because the corner stays
    /// put while everything around it grows.
    /// </summary>
    public void ZoomBy(int steps, Point atScreen)
    {
        if (steps == 0) return;
        var before = ToWorld(atScreen);
        Zoom = Math.Clamp(Zoom + steps, MinZoom, MaxZoom);
        var after = ToWorld(atScreen);
        Corner += new Point(before.X - after.X, before.Y - after.Y);
    }

    /// <summary>A point on the window, in world pixels.</summary>
    public Point ToWorld(Point screen) =>
        new(Corner.X + screen.X / Zoom, Corner.Y + screen.Y / Zoom);

    /// <summary>A point in the world, on the window.</summary>
    public Point ToScreen(Point world) =>
        new((world.X - Corner.X) * Zoom, (world.Y - Corner.Y) * Zoom);
}
