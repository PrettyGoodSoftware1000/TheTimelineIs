using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Iso;

namespace TheTimelineIs.Core.Pixel;

/// <summary>
/// The stand-in for a character nobody has drawn yet: an isometric box with a
/// letter on its top face.
///
/// Drawn in code rather than shipped as files, because there is one per
/// character and they all change the moment the real art lands. The top face is
/// the same diamond the ground is, so a placeholder sits on the grid like
/// everything else — and it is as big as the BODY, so a Living Stone's box
/// covers its four squares and a Gator's covers its two. A one-square box
/// standing in for a four-square enemy told you nothing about the ground it
/// was blocking.
///
/// The letters are a three-by-five bitmap alphabet. A font would be sharper at
/// large sizes and unreadable at this one: at five pixels tall, letters have to
/// be placed by hand.
/// </summary>
public static class PlaceholderCube
{
    /// <summary>Height of the box's side walls, in pixels.</summary>
    private const int Walls = 12;

    /// <summary>How wide a box is for a body of the given footprint.</summary>
    public static Point SizeOf(int sizeX, int sizeY) => new(
        (sizeX + sizeY) * (IsoMath.TileW / 2),
        (sizeX + sizeY) * (IsoMath.TileH / 2) + Walls);

    public static Texture2D Make(GraphicsDevice device, char initial, Color tint,
        int sizeX = 1, int sizeY = 1)
    {
        var size = SizeOf(sizeX, sizeY);
        var tex = new Texture2D(device, size.X, size.Y);
        tex.SetData(Pixels(initial, tint, sizeX, sizeY));
        return tex;
    }

    /// <summary>
    /// The box's pixels, apart from any graphics card. Separated so the shape
    /// can be checked without a window: a box that comes out flat is a bug you
    /// want a test to catch, not something to squint at on screen.
    /// </summary>
    public static Color[] Pixels(char initial, Color tint, int sizeX = 1, int sizeY = 1)
    {
        var size = SizeOf(sizeX, sizeY);
        int w = size.X, h = size.Y;
        var px = new Color[w * h];              // starts fully transparent

        var top = Lighten(tint, 0.35f);
        var leftWall = Darken(tint, 0.30f);
        var rightWall = Darken(tint, 0.55f);

        // The top face: the footprint's own outline, which for one square is a
        // diamond and for a longer body is a diamond stretched along one axis.
        // A pixel is inside it when the square it sits over is inside the
        // footprint, which is the same sum-and-difference test the grid uses.
        int faceH = (sizeX + sizeY) * (IsoMath.TileH / 2);
        var lowest = new int[w];
        for (int x = 0; x < w; x++) lowest[x] = -1;

        for (int y = 0; y < faceH; y++)
            for (int x = 0; x < w; x++)
            {
                // where this pixel falls on the grid, in squares from the
                // body's top corner
                float u = (x - w / 2f) / (IsoMath.TileW / 2f);
                float v = (y - faceH / 2f) / (IsoMath.TileH / 2f);
                float gx = (u + v) / 2f + (sizeX - sizeY) / 4f + (sizeX + sizeY) / 4f;
                float gy = (v - u) / 2f - (sizeX - sizeY) / 4f + (sizeX + sizeY) / 4f;
                if (gx < 0 || gx >= sizeX || gy < 0 || gy >= sizeY) continue;
                px[y * w + x] = top;
                lowest[x] = y;
            }

        // Side walls hang off the face's lower edges — every column gets a
        // solid run of them, or the box comes out hollow. The left catches more
        // light than the right, which is what makes it read as a solid object.
        for (int x = 0; x < w; x++)
        {
            if (lowest[x] < 0) continue;
            for (int y = lowest[x] + 1; y <= lowest[x] + Walls && y < h; y++)
                px[y * w + x] = x < w / 2 ? leftWall : rightWall;
        }

        Stamp(px, w, h, initial, w / 2 - 2, faceH / 2 - 2, ReadableOn(top));
        return px;
    }

    /// <summary>Black on a pale box, white on a dark one, so the letter always shows.</summary>
    private static Color ReadableOn(Color c) =>
        c.R * 0.299f + c.G * 0.587f + c.B * 0.114f > 140f ? Color.Black : Color.White;

    private static Color Lighten(Color c, float by) => new(
        (int)(c.R + (255 - c.R) * by), (int)(c.G + (255 - c.G) * by), (int)(c.B + (255 - c.B) * by));

    private static Color Darken(Color c, float by) => new(
        (int)(c.R * (1f - by)), (int)(c.G * (1f - by)), (int)(c.B * (1f - by)));

    private static void Stamp(Color[] px, int w, int h, char letter, int x0, int y0, Color ink)
    {
        string[] glyph = Glyph(letter);
        for (int y = 0; y < glyph.Length; y++)
            for (int x = 0; x < glyph[y].Length; x++)
            {
                if (glyph[y][x] != '#') continue;
                int px0 = x0 + x, py0 = y0 + y;
                if (px0 >= 0 && px0 < w && py0 >= 0 && py0 < h)
                    px[py0 * w + px0] = ink;
            }
    }

    /// <summary>
    /// A 3x5 letter. Narrow on purpose: the top face of a single square is only
    /// 16 pixels tall and the letter has to sit inside it without touching an
    /// edge. Anything with no glyph drawn falls back to a filled box, which
    /// still says "this one has no art" without pretending to be a letter.
    /// </summary>
    private static string[] Glyph(char c) => Letters.TryGetValue(c, out var g) ? g : Box;

    private static readonly string[] Box = { "###", "# #", "# #", "# #", "###" };

    private static readonly Dictionary<char, string[]> Letters = new()
    {
        ['A'] = new[] { " # ", "# #", "###", "# #", "# #" },
        ['B'] = new[] { "## ", "# #", "## ", "# #", "## " },
        ['C'] = new[] { " ##", "#  ", "#  ", "#  ", " ##" },
        ['D'] = new[] { "## ", "# #", "# #", "# #", "## " },
        ['E'] = new[] { "###", "#  ", "## ", "#  ", "###" },
        ['F'] = new[] { "###", "#  ", "## ", "#  ", "#  " },
        ['G'] = new[] { " ##", "#  ", "# #", "# #", " ##" },
        ['H'] = new[] { "# #", "# #", "###", "# #", "# #" },
        ['I'] = new[] { "###", " # ", " # ", " # ", "###" },
        ['J'] = new[] { "  #", "  #", "  #", "# #", " # " },
        ['K'] = new[] { "# #", "## ", "#  ", "## ", "# #" },
        ['L'] = new[] { "#  ", "#  ", "#  ", "#  ", "###" },
        ['M'] = new[] { "# #", "###", "###", "# #", "# #" },
        ['N'] = new[] { "# #", "###", "###", "###", "# #" },
        ['O'] = new[] { " # ", "# #", "# #", "# #", " # " },
        ['P'] = new[] { "## ", "# #", "## ", "#  ", "#  " },
        ['Q'] = new[] { " # ", "# #", "# #", "## ", " ##" },
        ['R'] = new[] { "## ", "# #", "## ", "# #", "# #" },
        ['S'] = new[] { " ##", "#  ", " # ", "  #", "## " },
        ['T'] = new[] { "###", " # ", " # ", " # ", " # " },
        ['U'] = new[] { "# #", "# #", "# #", "# #", " # " },
        ['V'] = new[] { "# #", "# #", "# #", "# #", " # " },
        ['W'] = new[] { "# #", "# #", "###", "###", "# #" },
        ['X'] = new[] { "# #", "# #", " # ", "# #", "# #" },
        ['Y'] = new[] { "# #", "# #", " # ", " # ", " # " },
        ['Z'] = new[] { "###", "  #", " # ", "#  ", "###" },
    };
}
