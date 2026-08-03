using System;
using System.Collections.Generic;
using System.Linq;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// Parses Content/Cast/PlayerCharacters/Classes.txt, the authoritative list of
/// player classes. "Class:" declares one. An optional "Card Tags:" line says
/// which card tags that class can play; with none declared a class plays cards
/// tagged with its own name, so a tag is a label a class opts into rather than
/// a class name in disguise. "Required Trait:" is recorded but unused for now.
/// </summary>
public class ClassLibrary
{
    private readonly Dictionary<string, List<string>> _cardTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    public const string Path = "Content/Cast/PlayerCharacters/Classes.txt";

    public IReadOnlyList<string> ClassNames => _order;

    /// <summary>Line in Classes.txt each class was declared on, for error messages.</summary>
    private readonly Dictionary<string, int> _lines = new(StringComparer.OrdinalIgnoreCase);
    public int LineOf(string className) => _lines.TryGetValue(className, out int l) ? l : 0;

    public static ClassLibrary Load()
    {
        var lib = new ClassLibrary();
        string? current = null;
        foreach (var (lineNo, raw) in AssetLoader.ReadNumbered(Path, Path))
        {
            string line = TextUtil.Clean(raw);
            if (line.StartsWith("Class:", StringComparison.OrdinalIgnoreCase))
            {
                current = line["Class:".Length..].Trim();
                if (current.Length == 0)
                {
                    Diagnostics.Current.Error(Path, lineNo, "'Class:' has no name after it");
                    continue;
                }
                if (!lib._cardTags.ContainsKey(current))
                {
                    lib._cardTags[current] = new List<string>();
                    lib._order.Add(current);
                    lib._lines[current] = lineNo;
                }
                else
                {
                    Diagnostics.Current.Warn(Path, lineNo, $"class '{current}' is declared twice");
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

    /// <summary>
    /// The tags this class can play. With no "Card Tags:" line it plays cards
    /// tagged with its own name; declaring tags replaces that entirely.
    /// </summary>
    public IReadOnlyList<string> CardTagsFor(string className) =>
        _cardTags.TryGetValue(className, out var tags) && tags.Count > 0
            ? tags : new List<string> { className };

    /// <summary>Every tag any declared class can play — what a card may legally be tagged with.</summary>
    public HashSet<string> AllPlayableTags() =>
        new(_order.SelectMany(CardTagsFor), StringComparer.OrdinalIgnoreCase);

    /// <summary>Declared classes that also have character art, i.e. the party picker's roster.</summary>
    public List<string> PlayableClasses() =>
        _order.Where(n => AssetLoader.Exists($"Content/Cast/PlayerCharacters/{n}/{n}.txt")).ToList();
}
