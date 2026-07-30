using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Data;

public record Destination(string Name, int X, int Y, string Mission);

/// <summary>
/// Parses Content/Missions/Destinations.txt. Columns are whitespace-separated;
/// the LAST three tokens are x, y, mission, and everything before them is the
/// destination name — so names may contain spaces. '#' lines are comments.
/// Coordinates are in map-image pixel space, not screen space.
/// </summary>
public class DestinationTable
{
    public List<Destination> All { get; } = new();

    public const string Path = "Content/Missions/Destinations.txt";

    public static DestinationTable Load()
    {
        var table = new DestinationTable();
        try
        {
            using var stream = TitleContainer.OpenStream(Path);
            using var reader = new StreamReader(stream);
            string? line;
            int lineNo = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNo++;
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                    continue;
                var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 4 ||
                    !int.TryParse(tokens[^3], out int x) ||
                    !int.TryParse(tokens[^2], out int y))
                {
                    Console.WriteLine($"[destinations] skipping malformed line {lineNo}: {line}");
                    continue;
                }
                string name = string.Join(' ', tokens[..^3]);
                table.All.Add(new Destination(name, x, y, tokens[^1]));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[destinations] failed to load {Path}: {ex.Message}");
        }
        return table;
    }
}
