using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// Content/Config.txt: art scale tuning. Lines look like "Global scale: 100%".
/// Resolution is OVERRIDE, not multiply — the most specific line wins whole:
/// a character's own line beats "Cast scale", which beats "Global scale".
/// UI (buttons, text, dialogue box) and the F12 ruler never scale.
/// </summary>
public class GameConfig
{
    private readonly Dictionary<string, float> _scales = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex Line = new(@"^(.+?)\s+scale\s*:\s*(\d+(?:\.\d+)?)\s*%\s*$",
        RegexOptions.IgnoreCase);

    public static GameConfig Load()
    {
        var cfg = new GameConfig();
        const string path = "Content/Config.txt";
        foreach (var (lineNo, raw) in AssetLoader.ReadNumbered(path, path))
        {
            var m = Line.Match(TextUtil.Clean(raw));
            if (m.Success)
                cfg._scales[m.Groups[1].Value.Trim()] =
                    float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 100f;
            else
                Diagnostics.Current.Error(path, lineNo,
                    $"unrecognized line '{raw}' — expected something like 'Cast scale: 90%'");
        }
        return cfg;
    }

    /// <summary>Applies to non-cast art: the map and room backgrounds.</summary>
    public float GlobalScale => _scales.TryGetValue("Global", out var v) ? v : 1f;

    /// <summary>Most specific wins: "{name} scale" > "Cast scale" > "Global scale".</summary>
    public float CastScale(string name) =>
        _scales.TryGetValue(name, out var v) ? v :
        _scales.TryGetValue("Cast", out var c) ? c : GlobalScale;
}
