using TheTimelineIs.Core.Data;

namespace TheTimelineIs.Core.Pixel;

/// <summary>
/// The picture a card throws.
///
/// Two shapes, told apart by the name on the card's `Projectile Art:` line:
///
///   Ball.png   one picture, turned on screen to point along its flight
///   Fireball   a FOLDER of eight compass rotations, already drawn turned
///
/// A folder is drawn upright and chosen by direction, because art drawn from
/// an angle looks wrong spun — a lawnmower on its side is a lawnmower on its
/// side. A direction with no file falls back to the nearest that has one, so
/// four rotations are enough to ship with.
/// </summary>
public static class EffectArt
{
    public const string Folder = "Content/Images/Pixel/Effects";

    /// <summary>What a card with no art of its own throws.</summary>
    public const string Fallback = "Ball.png";

    /// <summary>The rotation file for one direction of a folder of art.</summary>
    public static string Path(string name, Facing8 facing) =>
        $"{Folder}/{name}/{facing.FileName()}.png";

    /// <summary>
    /// The rotation to draw for a direction, or null when this name is one
    /// picture rather than a folder of them. Walks out from the direction
    /// asked for, the same way a character's missing pose does.
    /// </summary>
    public static string? Turned(string name, Facing8 facing)
    {
        if (name.Length == 0 || name.Contains('.')) return null;
        for (int step = 0; step <= 4; step++)
            foreach (int side in step == 0 ? new[] { 0 } : new[] { step, -step })
            {
                var near = Facings.All[(((int)facing + side) % 8 + 8) % 8];
                string path = Path(name, near);
                if (AssetLoader.Exists(path)) return path;
            }
        return null;
    }

    /// <summary>True when this name is a folder with any rotation in it.</summary>
    public static bool IsTurned(string name) => Turned(name, Facings.Default) != null;

    /// <summary>The one picture for a name, or the ball when it isn't there.</summary>
    public static string Single(string name)
    {
        string mine = $"{Folder}/{name}";
        return AssetLoader.Exists(mine) ? mine : $"{Folder}/{Fallback}";
    }
}
