using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using TheTimelineIs.Core.Data;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// A terrain set: sixteen tiles that blend two terrains into each other, the
/// way PixelLab exports them.
///
/// WHAT THE SIXTEEN ARE. Each tile is one square, and which picture a square
/// gets depends on its four CORNERS, not on the square itself. A corner is
/// either terrain A or terrain B, so four corners make sixteen combinations,
/// and the number of the tile IS the combination read as four bits:
///
///     bit 0 (1) = the south corner, at (x+1, y+1)
///     bit 1 (2) = the west  corner, at (x,   y+1)
///     bit 2 (4) = the east  corner, at (x+1, y)
///     bit 3 (8) = the north corner, at (x,   y)
///
/// A bit that is 0 is terrain A, 1 is terrain B. So mask 0 is all A, mask 15
/// is all B, and the fourteen in between are the edges and corners where one
/// runs into the other. That bit order is not a guess: it was read off two
/// PixelLab maps by finding the only assignment on which every pair of
/// neighbours agreed about the corners they share.
///
/// WHY SQUARES STORE THE MASK. A level writes "StoneToGrass:6" on a square —
/// the set and the finished mask — rather than storing terrains and working
/// the mask out at load. Same reason the editor writes down which piece a
/// Random brush picked: what you saw when you painted is what loads back.
///
/// SETS DO NOT BLEND INTO EACH OTHER. A set joins exactly two terrains. Where
/// a stone-to-grass square meets a wood-to-rug one there is a hard edge,
/// because no art exists for that join. Draw a wood-to-stone set later and the
/// editor finds it by its two terrain names and starts using it.
/// </summary>
public class TerrainSet
{
    /// <summary>Folder name under Content/Images/Terrain, and the name levels use.</summary>
    public string Name = "";

    /// <summary>The two terrains this set joins. A is mask 0, B is mask 15.</summary>
    public string A = "", B = "";

    /// <summary>The top face: 96 x 48 for a 2:1 grid.</summary>
    public int TileW = IsoMath.TileW, TileH = IsoMath.TileH;

    /// <summary>
    /// How far the side walls hang below the top face. The picture is this much
    /// taller than the face, and one step of height is this many pixels.
    /// </summary>
    public int Wall;

    /// <summary>Line in Terrain.txt where this set starts, for error messages.</summary>
    public int Line;

    /// <summary>The picture for one of the sixteen, e.g. "StoneToGrass/mask-06.png".</summary>
    public string FileFor(int mask) => $"{Name}/mask-{mask:00}.png";

    /// <summary>What a square writes in a level file.</summary>
    public string TokenFor(int mask) => $"{Name}:{mask}";

    /// <summary>
    /// The pixel of the picture that lands on the centre of the square's top
    /// face. Always the middle of the top face, so nobody has to anchor a set
    /// by hand — every tile in every set is anchored the same way.
    /// </summary>
    public Point Anchor => new(TileW / 2, TileH / 2);

    /// <summary>True when this set joins these two terrains, in either order.</summary>
    public bool Joins(string one, string other) =>
        (A.Equals(one, StringComparison.OrdinalIgnoreCase) &&
         B.Equals(other, StringComparison.OrdinalIgnoreCase)) ||
        (A.Equals(other, StringComparison.OrdinalIgnoreCase) &&
         B.Equals(one, StringComparison.OrdinalIgnoreCase));

    /// <summary>Which side of this set a terrain sits on: 0 for A, 1 for B, -1 for neither.</summary>
    public int SideOf(string terrain) =>
        A.Equals(terrain, StringComparison.OrdinalIgnoreCase) ? 0
        : B.Equals(terrain, StringComparison.OrdinalIgnoreCase) ? 1
        : -1;

    /// <summary>The terrain on a given corner of a mask.</summary>
    public string TerrainAt(int mask, Corner corner) =>
        ((mask >> (int)corner) & 1) == 1 ? B : A;

    /// <summary>A square covered entirely by one terrain, or -1 if it isn't ours.</summary>
    public int SolidMask(string terrain) => SideOf(terrain) switch
    {
        0 => 0,
        1 => 15,
        _ => -1,
    };
}

/// <summary>
/// The four corners of a square, numbered as the bit they occupy in a mask.
/// The names are the compass direction the corner points on screen.
/// </summary>
public enum Corner { South = 0, West = 1, East = 2, North = 3 }

public static class Corners
{
    public static readonly Corner[] All = { Corner.South, Corner.West, Corner.East, Corner.North };

    /// <summary>
    /// Where a corner of the square at (x, y) sits on the corner grid. Corners
    /// live on their own lattice, one wider and one taller than the squares,
    /// because the corner at (x, y) is shared by the four squares around it.
    /// </summary>
    public static Point Of(Point square, Corner corner) => corner switch
    {
        Corner.North => square,
        Corner.East => new Point(square.X + 1, square.Y),
        Corner.West => new Point(square.X, square.Y + 1),
        _ => new Point(square.X + 1, square.Y + 1),
    };

    /// <summary>The four squares that touch a corner — the inverse of Of().</summary>
    public static IEnumerable<(Point Square, Corner Corner)> Touching(Point corner)
    {
        yield return (corner, Corner.North);
        yield return (new Point(corner.X - 1, corner.Y), Corner.East);
        yield return (new Point(corner.X, corner.Y - 1), Corner.West);
        yield return (new Point(corner.X - 1, corner.Y - 1), Corner.South);
    }

    /// <summary>Builds a mask from the terrain on each corner, for one set.</summary>
    public static int MaskFrom(TerrainSet set, Func<Corner, string> terrainAt)
    {
        int mask = 0;
        foreach (var c in All)
            if (set.SideOf(terrainAt(c)) == 1) mask |= 1 << (int)c;
        return mask;
    }
}
