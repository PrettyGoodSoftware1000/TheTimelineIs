using System;
using System.Collections.Generic;
using System.Linq;

namespace TheTimelineIs.Core.Data;

/// <summary>One playable class: stats and sprites in a single Classes.txt block.</summary>
public class PlayerClass
{
    public string Name = "";
    public int Hp = 20;
    public int Movement = 5;
    public List<string> Sprites = new();   // defaults to {Name}.png
    public List<string> CardTags = new();  // defaults to the class's own name
    public int Line;

    public string Folder => $"Content/Cast/PlayerCharacters/{Name}";
    public IReadOnlyList<string> SpriteFiles =>
        Sprites.Count > 0 ? Sprites : new List<string> { $"{Name}.png" };
}

/// <summary>
/// Parses Content/Cast/PlayerCharacters/Classes.txt, the single authoritative
/// class file — the old per-character {Name}.txt manifests are gone. Format:
///
///   Class: Dirtbag
///   HP: 15
///   Movement: 8
///   Sprites: Dirtbag.png            (optional; defaults to {Name}.png)
///   Card Tags: Dirtbag, 'Mancer     (optional; defaults to the class name)
/// </summary>
public class ClassLibrary
{
    private readonly Dictionary<string, PlayerClass> _classes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    public const string Path = "Content/Cast/PlayerCharacters/Classes.txt";

    /// <summary>Set by Load; lets the old cast-resolution path see the same data.</summary>
    public static ClassLibrary Current { get; private set; } = new();

    public IReadOnlyList<string> ClassNames => _order;
    public PlayerClass? Get(string name) => _classes.TryGetValue(name, out var c) ? c : null;
    public int LineOf(string name) => Get(name)?.Line ?? 0;

    public static ClassLibrary Load()
    {
        var diag = Diagnostics.Current;
        var lib = new ClassLibrary();
        PlayerClass? current = null;

        foreach (var (lineNo, raw) in AssetLoader.ReadNumbered(Path, Path))
        {
            string line = TextUtil.Clean(raw);
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                diag.Error(Path, lineNo, $"unrecognized line '{line}' — expected 'Key: value'");
                continue;
            }
            string key = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();

            if (key == "class")
            {
                if (value.Length == 0) { diag.Error(Path, lineNo, "'Class:' has no name"); continue; }
                if (lib._classes.ContainsKey(value))
                {
                    diag.Warn(Path, lineNo, $"class '{value}' is declared twice");
                    current = lib._classes[value];
                    continue;
                }
                current = new PlayerClass { Name = value, Line = lineNo };
                lib._classes[value] = current;
                lib._order.Add(value);
                continue;
            }
            if (current == null)
            {
                diag.Error(Path, lineNo, $"'{key}' appears before any 'Class:' line");
                continue;
            }
            switch (key)
            {
                case "hp":
                    if (int.TryParse(value, out int hp) && hp > 0) current.Hp = hp;
                    else diag.Error(Path, lineNo, $"'{current.Name}': HP must be a positive number, got '{value}'");
                    break;
                case "movement":
                    if (int.TryParse(value, out int mv) && mv > 0) current.Movement = mv;
                    else diag.Error(Path, lineNo, $"'{current.Name}': Movement must be a positive number, got '{value}'");
                    break;
                case "sprites":
                    current.Sprites = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    break;
                case "card tags":
                    current.CardTags = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    break;
                default:
                    diag.Warn(Path, lineNo, $"'{current.Name}': unknown line '{line}' ignored");
                    break;
            }
        }
        Current = lib;
        return lib;
    }

    /// <summary>The tags this class can play; its own name when none are declared.</summary>
    public IReadOnlyList<string> CardTagsFor(string className)
    {
        var cls = Get(className);
        return cls != null && cls.CardTags.Count > 0
            ? cls.CardTags : new List<string> { className };
    }

    /// <summary>Every tag any class can play — what a card may legally be tagged with.</summary>
    public HashSet<string> AllPlayableTags() =>
        new(_order.SelectMany(CardTagsFor), StringComparer.OrdinalIgnoreCase);

    /// <summary>Declared classes whose first sprite actually exists — the party picker's roster.</summary>
    public List<string> PlayableClasses() =>
        _order.Where(n => Get(n) is PlayerClass c &&
            AssetLoader.Exists($"{c.Folder}/{c.SpriteFiles[0]}")).ToList();
}
