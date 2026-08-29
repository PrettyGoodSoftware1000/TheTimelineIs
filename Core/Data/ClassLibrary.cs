using System;
using System.Collections.Generic;
using System.Linq;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// A shape a class can wear: its own name, its own art, and optionally its own
/// casting animation. A blank animation falls back to the class's own line.
/// </summary>
public record ClassForm(string Name, string Sprite, string Animation = "");

/// <summary>One playable class: stats, sprites and forms in a Classes.txt block.</summary>
public class PlayerClass
{
    public string Name = "";
    public int Hp = 20;
    public int Movement = 5;

    /// <summary>
    /// Action points granted each turn once a fight starts. Unspent points
    /// carry over with no ceiling, so this is a rate rather than a budget.
    /// </summary>
    public int Actions = CharacterInstance.DefaultActionsPerTurn;
    public List<string> Sprites = new();   // defaults to {Name}.png
    public List<string> CardTags = new();  // defaults to the class's own name
    /// <summary>Declared with "Form: Name, Art.png". The first one is where the class starts.</summary>
    public List<ClassForm> Forms = new();
    /// <summary>
    /// Sheet played while this class casts a card, relative to its own folder.
    /// A form carrying its own animation overrides it — the wolf never plays
    /// the witch's spell.
    /// </summary>
    public string CastAnimation = "";
    public int Line;

    public string Folder => $"Content/Cast/PlayerCharacters/{Name}";

    /// <summary>Sprites the class can wear: its forms' art, or its plain sprite list.</summary>
    public IReadOnlyList<string> SpriteFiles =>
        Forms.Count > 0 ? Forms.Select(f => f.Sprite).ToList()
        : Sprites.Count > 0 ? Sprites
        : new List<string> { $"{Name}.png" };

    public string StartingForm => Forms.Count > 0 ? Forms[0].Name : "";

    public ClassForm? FindForm(string name) =>
        Forms.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Art for a form, falling back to the class's first sprite.</summary>
    public string SpriteForForm(string form) =>
        FindForm(form)?.Sprite ?? SpriteFiles[0];

    /// <summary>
    /// The casting sheet to play in a given shape, as a full content path, or
    /// null when neither the form nor the class declares one. Most specific
    /// wins, the same rule Config.txt scales follow.
    /// </summary>
    public string? CastAnimationPath(string form)
    {
        string file = FindForm(form)?.Animation is { Length: > 0 } own ? own : CastAnimation;
        return file.Length > 0 ? $"{Folder}/{file}" : null;
    }

    /// <summary>Every animation this class can ever play, for the startup check.</summary>
    public IEnumerable<string> AllCastAnimationPaths()
    {
        if (CastAnimation.Length > 0) yield return $"{Folder}/{CastAnimation}";
        foreach (var form in Forms)
            if (form.Animation.Length > 0) yield return $"{Folder}/{form.Animation}";
    }
}

/// <summary>
/// Parses Content/Cast/PlayerCharacters/Classes.txt, the single authoritative
/// class file — the old per-character {Name}.txt manifests are gone. Format:
///
///   Class: Dirtbag
///   HP: 15
///   Movement: 8
///   Actions: 5                 (optional; points a turn, default 5)
///   Sprites: Dirtbag.png            (optional; defaults to {Name}.png)
///   Card Tags: Dirtbag, 'Mancer     (optional; defaults to the class name)
///   Cast Animation: Spell/Spell.png (optional; the sheet played while this
///                                    class casts, relative to its own folder)
///   Form: Werewolf, Wolf.png        (optional, repeatable; the first is the
///                                    starting form. Cards with a matching
///                                    "Form:" line only appear in that form.)
///   Form: Witch, Witch.png, Cast/Cast.png
///                                   (a third field gives that shape its own
///                                    casting animation, beating the class's)
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
                case "actions":
                    if (int.TryParse(value, out int ap) && ap > 0) current.Actions = ap;
                    else diag.Error(Path, lineNo,
                        $"'{current.Name}': Actions must be a positive number of points a turn, got '{value}'");
                    break;
                case "sprites":
                    current.Sprites = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    break;
                case "form":
                    // "Form: Werewolf, WerewitchWerewolf.png" — and optionally a
                    // third field, the casting sheet this shape plays
                    var bits = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (bits.Length < 2)
                        diag.Error(Path, lineNo,
                            $"'{current.Name}': Form needs a name and an image, e.g. 'Form: Witch, Witch.png'");
                    else if (current.Forms.Exists(f => f.Name.Equals(bits[0], StringComparison.OrdinalIgnoreCase)))
                        diag.Warn(Path, lineNo, $"'{current.Name}': form '{bits[0]}' is declared twice");
                    else
                        current.Forms.Add(new ClassForm(bits[0], bits[1],
                            bits.Length > 2 ? bits[2] : ""));
                    break;
                case "cast animation":
                    current.CastAnimation = value;
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
