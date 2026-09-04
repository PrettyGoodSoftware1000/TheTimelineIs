using System;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Pixel;

/// <summary>
/// Which way a character is turned, named for where they FACE on screen.
///
/// South-east is the front of the character looking towards the bottom-right
/// of the screen; north is their back, looking away up the screen. The names
/// are the file names in a character's rotations folder.
/// </summary>
public enum Facing8
{
    North, NorthEast, East, SouthEast, South, SouthWest, West, NorthWest,
}

public static class Facings
{
    /// <summary>What a character faces when a level opens.</summary>
    public const Facing8 Default = Facing8.SouthEast;

    /// <summary>The file name for a direction: "north-east", "south", and so on.</summary>
    public static string FileName(this Facing8 f) => f switch
    {
        Facing8.North => "north",
        Facing8.NorthEast => "north-east",
        Facing8.East => "east",
        Facing8.SouthEast => "south-east",
        Facing8.South => "south",
        Facing8.SouthWest => "south-west",
        Facing8.West => "west",
        _ => "north-west",
    };

    /// <summary>Every direction, in compass order.</summary>
    public static readonly Facing8[] All =
    {
        Facing8.North, Facing8.NorthEast, Facing8.East, Facing8.SouthEast,
        Facing8.South, Facing8.SouthWest, Facing8.West, Facing8.NorthWest,
    };

    /// <summary>
    /// Where one square sits relative to another, in SCREEN terms rather than
    /// grid terms.
    ///
    /// The grid is turned 45 degrees on screen, so a step along +X is not
    /// "east" to the player looking at it — it goes down and to the right,
    /// which is south-east. Everything here works in screen space so the names
    /// mean what somebody looking at the screen would say.
    /// </summary>
    public static Facing8 Towards(Point from, Point to)
    {
        // the same projection the tiles use, without the height
        float dx = (to.X - from.X) - (to.Y - from.Y);
        float dy = ((to.X - from.X) + (to.Y - from.Y)) * 0.5f;
        if (dx == 0 && dy == 0) return Default;

        // screen y grows downwards, so a positive dy is southward
        double angle = Math.Atan2(dy, dx);                   // -pi..pi, 0 = east
        int step = (int)Math.Round(angle / (Math.PI / 4));   // eighths of a turn
        int eighth = ((step % 8) + 8) % 8;
        return eighth switch
        {
            0 => Facing8.East,
            1 => Facing8.SouthEast,
            2 => Facing8.South,
            3 => Facing8.SouthWest,
            4 => Facing8.West,
            5 => Facing8.NorthWest,
            6 => Facing8.North,
            _ => Facing8.NorthEast,
        };
    }

    /// <summary>
    /// Where a character faces after walking from one square to another.
    ///
    /// Walking only ever leaves somebody on one of the four diagonals. That is
    /// not a restriction so much as what the grid does: a step along a grid
    /// axis IS a screen diagonal, and those four are the poses a walk can end
    /// in. A step that moves on both grid axes at once is settled by whichever
    /// moved further, and a tie keeps the facing rather than picking for you.
    /// </summary>
    public static Facing8 Walking(Point from, Point to, Facing8 now)
    {
        int dx = to.X - from.X, dy = to.Y - from.Y;
        // dead level on both axes, including not having moved at all
        if (Math.Abs(dx) == Math.Abs(dy)) return now;
        return Math.Abs(dx) > Math.Abs(dy)
            ? (dx > 0 ? Facing8.SouthEast : Facing8.NorthWest)
            : (dy > 0 ? Facing8.SouthWest : Facing8.NorthEast);
    }
}
