using System;
using System.Collections.Generic;
using System.Linq;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// Parses Content/Cast/PlayerCharacters/Classes.txt: "Class:" starts a block,
/// "Card Tags:" lists which card tags that class can see and play.
/// "Required Trait:" lines are recorded but unused for now (character creation
/// isn't built yet).
/// </summary>
public class ClassLibrary
{
    private readonly Dictionary<string, List<string>> _cardTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    public const string Path = "Content/Cast/PlayerCharacters/Classes.txt";

    public IReadOnlyList<string> ClassNames => _order;

    public static ClassLibrary Load()
    {
        var lib = new ClassLibrary();
        string? current = null;
        foreach (var raw in AssetLoader.TryReadLines(Path))
        {
            string line = TextUtil.Clean(raw);
            if (line.StartsWith("Class:", StringComparison.OrdinalIgnoreCase))
            {
                current = line["Class:".Length..].Trim();
                if (!lib._cardTags.ContainsKey(current))
                {
                    lib._cardTags[current] = new List<string>();
                    lib._order.Add(current);
                }
            }
            else if (current != null && line.StartsWith("Card Tags:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var tag in line["Card Tags:".Length..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!lib._cardTags[current].Contains(tag, StringComparer.OrdinalIgnoreCase))
                        lib._cardTags[current].Add(tag);
            }
            // "Required Trait:" and anything else: ignored for now
        }
        return lib;
    }

    /// <summary>A class not in the file can still play cards tagged with its own name.</summary>
    public IReadOnlyList<string> CardTagsFor(string className) =>
        _cardTags.TryGetValue(className, out var tags) && tags.Count > 0
            ? tags : new List<string> { className };
}
