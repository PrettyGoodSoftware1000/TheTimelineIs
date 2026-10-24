using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Platform;

namespace TheTimelineIs.Core.Pixel;

/// <summary>
/// A character drawn from eight rotations, and whatever animations sit beside
/// them. Art is a folder per POSE — the picture an animation starts from —
/// holding that pose's directions and one folder per animation:
///
///   {Character}/{Form}/IdlePose/south-east.png
///   {Character}/{Form}/IdlePose/SpellCast/south-east/*.png
///
/// Both are read by LOOKING, not by being told: a pose is any folder with a
/// compass-named picture in it, and an animation is however many pictures are
/// in its folder, played in name order. Dropping frames in is all it takes.
/// Anything in brackets in an animation's folder name is a note and is
/// ignored, so "Running (Fix Edge)" plays as Running.
///
/// Everything is loaded once and cached. A direction with no file falls back to
/// the nearest one that has art, so a half-finished character still draws.
/// </summary>
public class DirectionalSprite
{
    /// <summary>The pose a character stands in, and the first one searched for animations.</summary>
    public const string IdlePose = "IdlePose";

    /// <summary>Moving out of a fight.</summary>
    public const string Walk = "Walk";

    /// <summary>Moving in a fight. "Running" is the same thing, spelled the other way.</summary>
    public static readonly string[] Run = { "Run", "Running" };

    /// <summary>
    /// The animation played when a card is cast and nothing more specific was
    /// asked for — not the card's own line, not the class's.
    /// </summary>
    public const string SpellCast = "SpellCast";

    /// <summary>The pose folders this character was read from, standing pose first.</summary>
    public IReadOnlyList<string> Poses { get; }

    private readonly Dictionary<Facing8, Texture2D> _rotations = new();
    private readonly Dictionary<string, Dictionary<Facing8, List<Texture2D>>> _animations =
        new(StringComparer.OrdinalIgnoreCase);

    public bool HasArt => _rotations.Count > 0;

    /// <summary>The names of every animation found, for reporting.</summary>
    public IEnumerable<string> AnimationNames => _animations.Keys;

    private DirectionalSprite(IReadOnlyList<string> poses) => Poses = poses;

    /// <summary>
    /// The pose folders for a character, starting from its folder and whatever
    /// its Art or Form line named. Empty when there is no art yet.
    ///
    /// The named folder is a pose itself if it has rotations in it. Otherwise
    /// every folder inside it that does is a pose — IdlePose first, since
    /// that is the one it stands in, then the rest by name. A Living Stone
    /// with an IdlePose and a Mid-walkPose stands idle and walks with the walk
    /// drawn from mid-stride.
    /// </summary>
    public static IReadOnlyList<string> PosesOf(IContentIndex index, string folder, string art)
    {
        string start = art.Length > 0 ? $"{folder}/{art}" : folder;
        if (IsPose(start)) return new[] { start };
        return index.Folders(start)
            .OrderBy(name => name.Equals(IdlePose, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(name => $"{start}/{name}")
            .Where(IsPose)
            .ToList();
    }

    private static bool IsPose(string folder) =>
        Facings.All.Any(f => AssetLoader.Exists($"{folder}/{f.FileName()}.png"));

    /// <summary>
    /// An animation's name from its folder's: "Running (Fix Edge)" is
    /// Running. Brackets are notes for whoever is drawing.
    /// </summary>
    public static string AnimationName(string folderName)
    {
        int open;
        while ((open = folderName.IndexOf('(')) >= 0)
        {
            int close = folderName.IndexOf(')', open);
            folderName = close < 0 ? folderName[..open] : folderName[..open] + folderName[(close + 1)..];
        }
        return folderName.Trim();
    }

    /// <summary>
    /// Every animation across the poses, by name, with the folder it lives
    /// in. Two poses with the same animation: the earlier pose wins.
    /// </summary>
    public static Dictionary<string, string> AnimationFolders(IContentIndex index, IReadOnlyList<string> poses)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pose in poses)
            foreach (string folder in index.Folders(pose))
            {
                string name = AnimationName(folder);
                if (name.Length > 0) found.TryAdd(name, $"{pose}/{folder}");
            }
        return found;
    }

    public static DirectionalSprite Load(AssetLoader assets, IContentIndex index, IReadOnlyList<string> poses)
    {
        var sprite = new DirectionalSprite(poses);
        if (poses.Count == 0) return sprite;

        foreach (var facing in Facings.All)
        {
            string path = $"{poses[0]}/{facing.FileName()}.png";
            if (!AssetLoader.Exists(path)) continue;
            sprite._rotations[facing] = assets.LoadTexture(path);
        }

        // An animation is a folder per direction under {Pose}/{Name}/.
        // Listing them means the exporter can name frames whatever it likes.
        foreach (var (name, animDir) in AnimationFolders(index, poses))
            foreach (var facing in Facings.All)
            {
                string dir = $"{animDir}/{facing.FileName()}";
                var frames = index.Images(dir);
                if (frames.Count == 0) continue;
                if (!sprite._animations.TryGetValue(name, out var byFacing))
                    sprite._animations[name] = byFacing = new Dictionary<Facing8, List<Texture2D>>();
                byFacing[facing] = frames.Select(f => assets.LoadTexture($"{dir}/{f}")).ToList();
            }
        return sprite;
    }

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
    public static float Fps = 14f;

    /// <summary>
    /// Frames for an animation in a direction, or null if there are none.
    ///
    /// A direction with no frames falls back to the nearest that has some,
    /// round the compass, the same way a missing rotation does. While a
    /// character has only one or two directions of an animation drawn, it
    /// still plays from every angle rather than only from the ones there are
    /// frames for.
    /// </summary>
    public IReadOnlyList<Texture2D>? Animation(string name, Facing8 facing)
    {
        if (!_animations.TryGetValue(AnimationName(name), out var byFacing)) return null;
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
    public bool HasAnimation(string name) => _animations.ContainsKey(AnimationName(name));
}
