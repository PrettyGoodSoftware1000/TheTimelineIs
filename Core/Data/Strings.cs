using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// All player-facing text lives in Content/text/strings.txt so the writer
/// can rework tone without touching code. Format: key = text, one per line.
/// </summary>
public class Strings
{
    private readonly Dictionary<string, string> _map = new();

    public static Strings Load()
    {
        var result = new Strings();
        try
        {
            using var stream = TitleContainer.OpenStream("Content/text/strings.txt");
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                    continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                result._map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[strings] failed to load Content/text/strings.txt: {ex.Message}");
        }
        return result;
    }

    /// <summary>Missing keys render as the key in brackets so they're obvious in-game.</summary>
    public string Get(string key) => _map.TryGetValue(key, out var v) ? v : $"[{key}]";
}
