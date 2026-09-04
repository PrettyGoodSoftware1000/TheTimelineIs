using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// Content/Config.txt: the overlay opacities the levels wash the ground with —
/// "Movement opacity: 20%" and friends. 0 is a real zero (outline only, no
/// fill), because switching a fill off is exactly what somebody would want.
///
/// There is no art scaling in here any more. Pixel art is drawn at its own
/// size, always; nothing about a picture is decided by a config line.
/// </summary>
public class GameConfig
{
    private readonly Dictionary<string, float> _opacities = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex OpacityLine =
        new(@"^(.+?)\s+opacity\s*:?\s*(\d+(?:\.\d+)?)\s*%?\s*$", RegexOptions.IgnoreCase);

    /// <summary>Fallbacks when Config.txt says nothing, as fractions of full.</summary>
    private static readonly Dictionary<string, float> Defaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Movement"] = 0.20f,
            ["Range"] = 0.18f,
            ["AoE"] = 0.30f,
            ["Cone"] = 0.30f,
            ["Leap"] = 0.18f,
            ["Trigger"] = 0.18f,
            ["Hover"] = 0.35f,
            ["Selected"] = 0.30f,
            ["Guard"] = 0.28f,
        };

    public const string Path = "Content/Config.txt";

    public static GameConfig Load()
    {
        var cfg = new GameConfig();
        foreach (var (lineNo, raw) in AssetLoader.ReadNumbered(Path, Path))
        {
            string clean = TextUtil.Clean(raw);
            var op = OpacityLine.Match(clean);
            if (!op.Success)
            {
                Diagnostics.Current.Error(Path, lineNo, clean.Contains("scale", StringComparison.OrdinalIgnoreCase)
                    ? $"'{raw}': art is not scaled any more — pixel art draws at its own size. Delete the line."
                    : $"unrecognized line '{raw}' — expected something like 'Movement opacity: 20%'");
                continue;
            }
            string key = op.Groups[1].Value.Trim();
            if (!Defaults.ContainsKey(key))
            {
                Diagnostics.Current.Error(Path, lineNo,
                    $"'{key} opacity' is not an overlay. Known: {string.Join(", ", Defaults.Keys)}");
                continue;
            }
            cfg._opacities[key] = Math.Clamp(
                float.Parse(op.Groups[2].Value, CultureInfo.InvariantCulture) / 100f, 0f, 1f);
        }
        return cfg;
    }

    /// <summary>
    /// How solid one of the ground overlays is painted, 0..1. The outline is
    /// always drawn at full strength; this is only the wash inside it.
    /// </summary>
    public float Opacity(string overlay) =>
        _opacities.TryGetValue(overlay, out var v) ? v :
        Defaults.TryGetValue(overlay, out var d) ? d : 0.2f;
}
