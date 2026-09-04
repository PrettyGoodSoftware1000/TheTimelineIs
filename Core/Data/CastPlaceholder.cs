using System;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// The bits of Classes.txt and Enemies.txt that both files share: the colour
/// a character's placeholder cube is painted, and the check that catches
/// somebody writing a picture's name where a folder's is wanted.
/// </summary>
public static class CastPlaceholder
{
    /// <summary>A plain grey, for anyone with no Colour line.</summary>
    public static readonly Color DefaultColour = new(120, 120, 130);

    /// <summary>"120, 80, 40" as a colour. Three whole numbers, each 0-255.</summary>
    public static bool TryParseColour(string text, out Color colour)
    {
        colour = DefaultColour;
        var bits = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (bits.Length != 3) return false;
        var rgb = new int[3];
        for (int i = 0; i < 3; i++)
            if (!int.TryParse(bits[i], out rgb[i]) || rgb[i] < 0 || rgb[i] > 255) return false;
        colour = new Color(rgb[0], rgb[1], rgb[2]);
        return true;
    }

    /// <summary>
    /// Whether a value is a file name rather than a folder name. Art used to
    /// be a picture, so the old lines are what somebody is most likely to
    /// write; naming the mistake beats a folder that silently isn't there.
    /// </summary>
    public static bool LooksLikeAPicture(string value)
    {
        foreach (var ext in Platform.IContentIndex.ImageTypes)
            if (value.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
