using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Platform;

namespace TheTimelineIs.Core.Pixel;

/// <summary>
/// Finds the pixel art for a character, and stands in for it when there is
/// none.
///
/// The art tool exports a folder per state with the eight rotations inside it,
/// but names that folder whatever the artist typed — "WitchForm" for one
/// character, the character's own name for another. So the state is found by
/// LOOKING for rotations rather than by knowing what it is called, and the
/// answer is remembered.
///
/// Anybody with no pixel art yet gets a placeholder cube instead of vanishing.
/// That is the normal state of this branch while the art is being drawn, so it
/// is built to be legible rather than to be an error: the cube carries the
/// character's initial and is tinted from their old painted picture, which is
/// enough to tell a Dirtbag from a Cyborg at a glance.
/// </summary>
public class CastSprites
{
    private readonly AssetLoader _assets;
    private readonly IContentIndex _index;
    private readonly GraphicsDevice _device;

    private readonly Dictionary<string, DirectionalSprite?> _sprites =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> _cubes =
        new(StringComparer.OrdinalIgnoreCase);

    public CastSprites(AssetLoader assets, IContentIndex index, GraphicsDevice device)
    {
        _assets = assets;
        _index = index;
        _device = device;
    }

    /// <summary>
    /// The rotations for a character, or null when they have no pixel art and
    /// should be drawn as a cube.
    /// </summary>
    public DirectionalSprite? For(CharacterInstance who)
    {
        string folder = who.Folder;
        if (_sprites.TryGetValue(folder, out var known)) return known;

        DirectionalSprite? found = null;
        if (StateFolder(folder) is string state)
        {
            var sprite = DirectionalSprite.Load(_assets, _index, folder, state);
            if (sprite.HasArt) found = sprite;
        }
        return _sprites[folder] = found;
    }

    /// <summary>The first state folder under a character that has rotations in it.</summary>
    private string? StateFolder(string characterFolder)
    {
        foreach (string state in _index.Folders(characterFolder))
            if (AssetLoader.Exists(
                    $"{characterFolder}/{state}/rotations/{Facings.Default.FileName()}.png"))
                return state;
        return null;
    }

    /// <summary>
    /// The stand-in cube for somebody with no pixel art: 32x32, their initial
    /// on the top face, coloured from whatever their old painted picture was
    /// mostly made of.
    /// </summary>
    public Texture2D Cube(CharacterInstance who)
    {
        string key = who.Folder + "|" + who.SpriteFile;
        if (_cubes.TryGetValue(key, out var made)) return made;
        return _cubes[key] = PlaceholderCube.Make(_device, InitialOf(who.Name), ColourOf(who));
    }

    private static char InitialOf(string name) =>
        string.IsNullOrWhiteSpace(name) ? '?' : char.ToUpperInvariant(name.Trim()[0]);

    /// <summary>
    /// The average of the old painted art, so the cube at least reminds you
    /// which character it stands for. Anything with no old art at all falls
    /// back to a plain grey.
    /// </summary>
    private Color ColourOf(CharacterInstance who)
    {
        if (!AssetLoader.Exists(who.SpritePath)) return new Color(120, 120, 130);
        return AverageColour(_assets.LoadTexture(who.SpritePath));
    }

    private static readonly Dictionary<Texture2D, Color> Averages = new();

    /// <summary>
    /// The mean of a picture's solid pixels. Transparent ones are skipped, or a
    /// character on a big empty canvas would come out the colour of nothing.
    /// </summary>
    private static Color AverageColour(Texture2D art)
    {
        if (Averages.TryGetValue(art, out var known)) return known;

        var pixels = new Color[art.Width * art.Height];
        art.GetData(pixels);
        long r = 0, g = 0, b = 0, n = 0;
        // a big painted sprite is millions of pixels and the answer is an
        // average, so a sample every few pixels gives the same colour far faster
        int step = Math.Max(1, pixels.Length / 20000);
        for (int i = 0; i < pixels.Length; i += step)
        {
            if (pixels[i].A < 128) continue;
            r += pixels[i].R; g += pixels[i].G; b += pixels[i].B; n++;
        }
        var mean = n == 0
            ? new Color(120, 120, 130)
            : new Color((int)(r / n), (int)(g / n), (int)(b / n));
        return Averages[art] = mean;
    }
}
