using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Platform;

namespace TheTimelineIs.Core.Pixel;

/// <summary>
/// A character drawn from eight rotations, and whatever animations sit beside
/// them, laid out the way the art tool exports:
///
///   {Character}/{State}/rotations/south-east.png
///   {Character}/{State}/animations/GunShot/east/*.png
///
/// Both are read by LOOKING, not by being told: the rotations are eight known
/// compass names, and an animation is however many pictures are in its folder,
/// played in name order. Dropping frames in is all it takes.
///
/// Everything is loaded once and cached. A direction with no file falls back to
/// the nearest one that has art, so a half-finished character still draws.
/// </summary>
public class DirectionalSprite
{
    /// <summary>Where this character's states live, e.g. ".../Werewitch".</summary>
    public string Folder { get; }

    /// <summary>Which state is loaded, e.g. "WitchForm".</summary>
    public string State { get; }

    private readonly Dictionary<Facing8, Texture2D> _rotations = new();
    private readonly Dictionary<string, Dictionary<Facing8, List<Texture2D>>> _animations =
        new(StringComparer.OrdinalIgnoreCase);

    public bool HasArt => _rotations.Count > 0;

    /// <summary>The names of every animation found, for reporting.</summary>
    public IEnumerable<string> AnimationNames => _animations.Keys;

    private DirectionalSprite(string folder, string state)
    {
        Folder = folder;
        State = state;
    }

    public static DirectionalSprite Load(AssetLoader assets, IContentIndex index,
        string folder, string state)
    {
        var sprite = new DirectionalSprite(folder, state);
        string root = $"{folder}/{state}";

        foreach (var facing in Facings.All)
        {
            string path = $"{root}/rotations/{facing.FileName()}.png";
            if (!AssetLoader.Exists(path)) continue;
            sprite._rotations[facing] = assets.LoadTexture(path);
        }

        // An animation is a folder per direction under animations/{Name}/.
        // Listing them means the exporter can name frames whatever it likes.
        string animRoot = $"{root}/animations";
        foreach (var facing in Facings.All)
        {
            foreach (string name in AnimationFolders(index, animRoot))
            {
                string dir = $"{animRoot}/{name}/{facing.FileName()}";
                var frames = index.Images(dir);
                if (frames.Count == 0) continue;
                if (!sprite._animations.TryGetValue(name, out var byFacing))
                    sprite._animations[name] = byFacing = new Dictionary<Facing8, List<Texture2D>>();
                byFacing[facing] = frames.Select(f => assets.LoadTexture($"{dir}/{f}")).ToList();
            }
        }
        return sprite;
    }

    /// <summary>
    /// The animation names under a state. There is no directory listing for
    /// folders, only for files, so this reads the names off the one level of
    /// paths the index can see.
    /// </summary>
    private static IEnumerable<string> AnimationFolders(IContentIndex index, string animRoot) =>
        index.Folders(animRoot);

    /// <summary>
    /// The picture for a direction. A missing one falls back to the nearest
    /// direction that has art, going round the compass, so a character with
    /// only a few angles drawn still appears instead of vanishing.
    /// </summary>
    public Texture2D? Rotation(Facing8 facing)
    {
        if (_rotations.TryGetValue(facing, out var exact)) return exact;
        for (int step = 1; step <= 4; step++)
            foreach (int side in new[] { step, -step })
            {
                var near = Facings.All[(((int)facing + side) % 8 + 8) % 8];
                if (_rotations.TryGetValue(near, out var found)) return found;
            }
        return null;
    }

    /// <summary>
    /// How many frames an animation shows a second. One number for every
    /// animation in the game, changeable from the ~ menu while looking at one.
    /// </summary>
    public static float Fps = 12f;

    /// <summary>
    /// Frames for an animation in a direction, or null if there are none.
    ///
    /// A direction with no frames falls back to the nearest that has some,
    /// round the compass, the same way a missing rotation does. While a
    /// character has one direction of one animation drawn — which is where
    /// the Gun-O-Mancer's GunShot is — it still plays from every angle rather
    /// than only when he happens to be facing east.
    /// </summary>
    public IReadOnlyList<Texture2D>? Animation(string name, Facing8 facing)
    {
        if (!_animations.TryGetValue(name, out var byFacing)) return null;
        if (byFacing.TryGetValue(facing, out var exact)) return exact;
        for (int step = 1; step <= 4; step++)
            foreach (int side in new[] { step, -step })
            {
                var near = Facings.All[(((int)facing + side) % 8 + 8) % 8];
                if (byFacing.TryGetValue(near, out var found)) return found;
            }
        return null;
    }

    /// <summary>Whether this animation exists in any direction at all.</summary>
    public bool HasAnimation(string name) => _animations.ContainsKey(name);
}
