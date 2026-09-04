using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace TheTimelineIs.Core.Pixel;

/// <summary>
/// The stand-in for a character nobody has drawn yet: a 32x32 isometric cube
/// with a letter on its top face.
///
/// Drawn in code rather than shipped as files, because there is one per
/// character and they all change the moment the real art lands. The shape is
/// the same 2:1 diamond as the ground, so a placeholder sits on the grid like
/// everything else, and the letter goes on the TOP face where the diamond is
/// widest and nothing overlaps it.
///
/// The letters are a five-by-five bitmap alphabet. A font would be sharper at
/// large sizes and unreadable at this one: at five pixels tall, letters have to
/// be placed by hand.
/// </summary>
public static class PlaceholderCube
{
    public const int Size = 32;

    /// <summary>Height of the cube's side walls, in pixels.</summary>
    private const int Walls = 12;

    public static Texture2D Make(GraphicsDevice device, char initial, Color tint)
    {
        var tex = new Texture2D(device, Size, Size);
        tex.SetData(Pixels(initial, tint));
        return tex;
    }

    /// <summary>
    /// The cube's pixels, apart from any graphics card. Separated so the shape
    /// can be checked without a window: a cube that comes out flat is a bug you
    /// want a test to catch, not something to squint at on screen.
    /// </summary>
    public static Color[] Pixels(char initial, Color tint)
    {
        var px = new Color[Size * Size];              // starts fully transparent

        // The top face: a 32x16 diamond sitting on top of the walls. Row y is
        // 4*(y+1) wide at the top half and mirrors below, which is the same
        // stepping the ground tiles use, so cube and floor line up exactly.
        var top = Lighten(tint, 0.35f);
        var leftWall = Darken(tint, 0.30f);
        var rightWall = Darken(tint, 0.55f);

        for (int y = 0; y < 16; y++)
        {
            int half = y < 8 ? (y + 1) * 2 : (16 - y) * 2;
            for (int x = Size / 2 - half; x < Size / 2 + half; x++)
                px[y * Size + x] = top;
        }

        // The two side walls hang off the diamond's lower edges — every column
        // gets a solid run of them, or the cube comes out hollow. The left wall
        // catches more light than the right, which is what makes it read as a
        // solid object rather than a flat hexagon.
        for (int x = 0; x < Size; x++)
        {
            // the lowest row of the top face in this column
            float fromCentre = Math.Abs(x - (Size / 2f - 0.5f));
            int bottom = (int)(16 - fromCentre / 2f) - 1;
            if (bottom < 0) continue;
            for (int y = bottom + 1; y <= bottom + Walls && y < Size; y++)
                px[y * Size + x] = x < Size / 2 ? leftWall : rightWall;
        }

        Stamp(px, initial, Size / 2 - 2, 5, ReadableOn(top));
        return px;
    }

    /// <summary>Black on a pale cube, white on a dark one, so the letter always shows.</summary>
    private static Color ReadableOn(Color c) =>
        c.R * 0.299f + c.G * 0.587f + c.B * 0.114f > 140f ? Color.Black : Color.White;

    private static Color Lighten(Color c, float by) => new(
        (int)(c.R + (255 - c.R) * by), (int)(c.G + (255 - c.G) * by), (int)(c.B + (255 - c.B) * by));

    private static Color Darken(Color c, float by) => new(
        (int)(c.R * (1f - by)), (int)(c.G * (1f - by)), (int)(c.B * (1f - by)));

    private static void Stamp(Color[] px, char letter, int x0, int y0, Color ink)
    {
        string[] glyph = Glyph(letter);
        for (int y = 0; y < glyph.Length; y++)
            for (int x = 0; x < glyph[y].Length; x++)
            {
                if (glyph[y][x] != '#') continue;
                int px0 = x0 + x, py0 = y0 + y;
                if (px0 >= 0 && px0 < Size && py0 >= 0 && py0 < Size)
                    px[py0 * Size + px0] = ink;
            }
    }

    /// <summary>
    /// A 3x5 letter. Narrow on purpose: the top face is only 16 pixels tall at
    /// its widest and the letter has to sit inside it without touching an edge.
    /// Anything with no glyph drawn falls back to a filled box, which still
    /// says "this one has no art" without pretending to be a letter.
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
