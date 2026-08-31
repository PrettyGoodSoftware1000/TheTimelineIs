using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
            ["Hover"] = 0.35f,
            ["Selected"] = 0.30f,
            ["Guard"] = 0.28f,
        };

    /// <summary>The one config file. There used to be a second copy nobody read.</summary>
    public const string Path = "Content/Config.txt";

    public static GameConfig Load()
    {
        var cfg = new GameConfig();
        const string path = Path;
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

    /// <summary>
    /// The scale written against this exact name, or 0 for "no line of its own".
    /// Unlike CastScale this does NOT fall through to Cast or Global — the
    /// scale menu needs to show what a line actually says, not what a character
    /// ends up drawn at.
    /// </summary>
    public float RawScale(string name) => _scales.TryGetValue(name, out var v) ? v : 0f;

    /// <summary>
    /// Sets one scale live. 0 removes the line, which puts that name back to
    /// falling through to Cast and then Global.
    /// </summary>
    public void SetScale(string name, float scale)
    {
        if (scale <= 0f) _scales.Remove(name);
        else _scales[name] = scale;
    }

    /// <summary>
    /// The whole file, ready to write back. The comment block at the top is
    /// rebuilt rather than preserved, so a file saved from the scale menu still
    /// explains itself to whoever opens it next.
    /// </summary>
    public string Serialize()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# How big art is drawn, as a percentage of its natural size.");
        sb.AppendLine("# - Most specific line wins outright: '<Name> <Form>' > '<Name>' > 'Cast' > 'Global'.");
        sb.AppendLine("# - 0 means IGNORE this line, so it falls through to the next one up.");
        sb.AppendLine("# - Ground tiles are never scaled; the grid decides their size.");
        sb.AppendLine("# - UI text, buttons and the F12 ruler never scale.");
        sb.AppendLine("#");
        sb.AppendLine("# Overlay opacity is how solid a ground wash is INSIDE its outline.");
        sb.AppendLine("# - The outline is always full strength, so 0 here means outline only.");
        sb.AppendLine("# - 0 is a real value here, not 'ignore'.");
        sb.AppendLine();
        foreach (var (name, value) in _scales.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"{name} scale: {Math.Round(value * 100f)}%");
        sb.AppendLine();
        foreach (string key in Defaults.Keys)
            sb.AppendLine($"{key} opacity: {Math.Round(Opacity(key) * 100f)}%");
        return sb.ToString();
    }

    /// <summary>Applies to non-cast art: the map and room backgrounds.</summary>
    public float GlobalScale => _scales.TryGetValue("Global", out var v) ? v : 1f;

    /// <summary>
    /// Most specific wins: "{name} {form} scale" > "{name} scale" > "Cast
    /// scale" > "Global scale".
    ///
    /// A shapeshifter's shapes are drawn from different art and rarely want the
    /// same size — a wolf on all fours is wider and shorter than the witch it
    /// turns from. "Werewitch Werewolf scale: 90%" sizes one shape without
    /// touching the other, and with no such line the class's own line applies
    /// to both exactly as before.
    /// </summary>
    public float CastScale(string name, string form = "") =>
        form.Length > 0 && _scales.TryGetValue($"{name} {form}", out var f) ? f :
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
