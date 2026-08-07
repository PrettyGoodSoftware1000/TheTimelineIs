using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// Content/Config.txt: art scale tuning. Lines look like "Global scale: 100%"
/// (the % is optional). Resolution is OVERRIDE, not multiply — the most
/// specific line wins whole: a character's own line beats "Cast scale", which
/// beats "Global scale".
///
/// A value of 0 means "ignore this line", so "Dirtbag scale: 0" falls through
/// to Cast scale exactly as if the line weren't there. That's how a line can
/// be switched off without deleting it.
///
/// UI (buttons, text, dialogue box) and the F12 ruler never scale.
///
/// The same file also carries the overlay opacities the isometric levels wash
/// the ground with — "Movement opacity: 20%" and friends. 0 there means a real
/// zero (outline only, no fill), not "ignore", because switching a fill off is
/// exactly what somebody would want.
/// </summary>
public class GameConfig
{
    private readonly Dictionary<string, float> _scales = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _opacities = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex Line = new(@"^(.+?)\s+scale\s*:?\s*(\d+(?:\.\d+)?)\s*%?\s*$",
        RegexOptions.IgnoreCase);

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
        };

    public static GameConfig Load()
    {
        var cfg = new GameConfig();
        const string path = "Content/Config.txt";
        foreach (var (lineNo, raw) in AssetLoader.ReadNumbered(path, path))
        {
            string clean = TextUtil.Clean(raw);

            var op = OpacityLine.Match(clean);
            if (op.Success)
            {
                string key = op.Groups[1].Value.Trim();
                if (!Defaults.ContainsKey(key))
                {
                    Diagnostics.Current.Error(path, lineNo,
                        $"'{key} opacity' is not an overlay. Known: {string.Join(", ", Defaults.Keys)}");
                    continue;
                }
                // 0 really means transparent here — an outline with no fill
                cfg._opacities[key] = Math.Clamp(
                    float.Parse(op.Groups[2].Value, CultureInfo.InvariantCulture) / 100f, 0f, 1f);
                continue;
            }

            var m = Line.Match(clean);
            if (!m.Success)
            {
                Diagnostics.Current.Error(path, lineNo,
                    $"unrecognized line '{raw}' — expected something like 'Cast scale: 90%' " +
                    "(or 0 to switch the line off), or 'Movement opacity: 20%'");
                continue;
            }
            float percent = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (percent <= 0f) continue;   // 0 = ignore, so the next level up applies
            cfg._scales[m.Groups[1].Value.Trim()] = percent / 100f;
        }
        return cfg;
    }

    /// <summary>Applies to non-cast art: the map and room backgrounds.</summary>
    public float GlobalScale => _scales.TryGetValue("Global", out var v) ? v : 1f;

    /// <summary>Most specific wins: "{name} scale" > "Cast scale" > "Global scale".</summary>
    public float CastScale(string name) =>
        _scales.TryGetValue(name, out var v) ? v :
        _scales.TryGetValue("Cast", out var c) ? c : GlobalScale;

    /// <summary>
    /// How solid one of the isometric ground overlays is painted, 0..1. The
    /// outline is always drawn at full strength; this is only the wash inside it.
    /// </summary>
    public float Opacity(string overlay) =>
        _opacities.TryGetValue(overlay, out var v) ? v :
        Defaults.TryGetValue(overlay, out var d) ? d : 0.2f;
}
