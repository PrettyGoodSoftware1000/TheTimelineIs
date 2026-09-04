using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Platform;

namespace TheTimelineIs.Core.Pixel;

/// <summary>
/// Finds the pixel art for a character, and stands in for it when there is
/// none.
///
/// Art is a folder per state inside the character's folder, with rotations/
/// and animations/ inside it. Which folder is looked up by the character's
/// form — a Werewitch in wolf shape asks for "WolfForm" — or, for anyone with
/// no Art line, the first folder that has rotations in it.
///
/// Anybody with no art yet gets a placeholder cube instead of vanishing. That
/// is the normal state of this branch while the art is being drawn, so it is
/// built to be legible rather than to be an error: the cube carries the
/// character's initial in the colour their Classes.txt or Enemies.txt block
/// gives them, which is enough to tell a Dirtbag from a Cyborg at a glance.
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
    /// The rotations for a character in its current form, or null when it has
    /// no pixel art and should be drawn as a cube.
    /// </summary>
    public DirectionalSprite? For(CharacterInstance who)
    {
        string key = who.Folder + "|" + who.Art;
        if (_sprites.TryGetValue(key, out var known)) return known;

        DirectionalSprite? found = null;
        string? state = who.Art.Length > 0 ? who.Art : FirstStateWithArt(who.Folder);
        if (state != null)
        {
            var sprite = DirectionalSprite.Load(_assets, _index, who.Folder, state);
            if (sprite.HasArt) found = sprite;
        }
        return _sprites[key] = found;
    }

    /// <summary>The first state folder under a character that has rotations in it.</summary>
    private string? FirstStateWithArt(string characterFolder)
    {
        foreach (string state in _index.Folders(characterFolder))
            if (AssetLoader.Exists(
                    $"{characterFolder}/{state}/rotations/{Facings.Default.FileName()}.png"))
                return state;
        return null;
    }

    /// <summary>
    /// The stand-in cube for somebody with no pixel art: 32x32, their initial
    /// on the top face, in their declared colour.
    /// </summary>
    public Texture2D Cube(CharacterInstance who)
    {
        string key = who.Name + "|" + who.Colour.PackedValue;
        if (_cubes.TryGetValue(key, out var made)) return made;
        return _cubes[key] = PlaceholderCube.Make(_device, InitialOf(who.Name), who.Colour);
    }

    /// <summary>
    /// The picture to draw for somebody standing still: their rotation for
    /// the way they face, or their cube.
    /// </summary>
    public Texture2D Standing(CharacterInstance who) =>
        For(who)?.Rotation(who.Facing.Nearest()) ?? Cube(who);

    /// <summary>
    /// A face for the HUD — the turn strip, the dialogue box, the party
    /// picker. The front-facing rotation, since that is the one that looks at
    /// the player, or the cube.
    /// </summary>
    public Texture2D Portrait(CharacterInstance who) =>
        For(who)?.Rotation(Facings.Default) ?? Cube(who);

    /// <summary>
    /// The frames of an animation for somebody, facing the way they are, or
    /// null when they have no such animation in a direction near enough.
    /// </summary>
    public IReadOnlyList<Texture2D>? Frames(CharacterInstance who, string animation) =>
        animation.Length == 0 ? null : For(who)?.Animation(animation, who.Facing);

    private static char InitialOf(string name) =>
        string.IsNullOrWhiteSpace(name) ? '?' : char.ToUpperInvariant(name.Trim()[0]);
}
