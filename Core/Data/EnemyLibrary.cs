using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Data;

/// <summary>One enemy kind, from a block of Enemies.txt.</summary>
public class EnemyDef
{
    public string Name = "";
    public int Hp = 10;
    public int Movement = 3;

    /// <summary>
    /// Action points granted each turn once a fight starts. At most one
    /// unspent point carries into the next turn, so this is close to a hard
    /// budget rather than an allowance to save up.
    /// </summary>
    public int Actions = CharacterInstance.DefaultActionsPerTurn;

    /// <summary>
    /// How many squares the body covers, across and down. "Size: 2" is 2x2 —
    /// a Living Stone. "Size: 2 x 1" is two squares side by side, which is a
    /// Gator: long rather than square.
    /// </summary>
    public int SizeX = 1, SizeY = 1;

    /// <summary>The longer side, for anything that wants one number.</summary>
    public int Size => Math.Max(SizeX, SizeY);

    /// <summary>Which state folder to draw from; empty means the first with rotations.</summary>
    public string Art = "";

    /// <summary>The colour of this enemy's placeholder cube while it has no art.</summary>
    public Color Colour = CastPlaceholder.DefaultColour;

    /// <summary>Which cards in EnemyCards.txt this enemy holds; its own name by default.</summary>
    public List<string> CardTags = new();

    /// <summary>The animation folder played while this enemy casts.</summary>
    public string CastAnimation = "";
    public int Line;

    public string Folder => $"Content/Cast/EnemyCharacters/{Name}";
}

/// <summary>
/// Parses Content/Cast/EnemyCharacters/Enemies.txt — one file for every enemy.
/// The format is documented at the top of that file.
/// </summary>
public class EnemyLibrary
{
    private readonly Dictionary<string, EnemyDef> _enemies = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    public const string Path = "Content/Cast/EnemyCharacters/Enemies.txt";

    public static EnemyLibrary Current { get; private set; } = new();

    public IReadOnlyList<string> EnemyNames => _order;
    public EnemyDef? Get(string name) => _enemies.TryGetValue(name, out var e) ? e : null;

    /// <summary>The tags this enemy's cards may be filed under; its own name by default.</summary>
    public IReadOnlyList<string> CardTagsFor(string name) =>
        Get(name) is EnemyDef d && d.CardTags.Count > 0 ? d.CardTags : new List<string> { name };

    /// <summary>Every tag any enemy holds — what an enemy card may legally be tagged with.</summary>
    public HashSet<string> AllTags() =>
        new(_order.SelectMany(CardTagsFor), StringComparer.OrdinalIgnoreCase);

    public static EnemyLibrary Load()
    {
        var diag = Diagnostics.Current;
        var lib = new EnemyLibrary();
        EnemyDef? current = null;

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

            if (key == "enemy")
            {
                if (value.Length == 0) { diag.Error(Path, lineNo, "'Enemy:' has no name"); continue; }
                if (lib._enemies.ContainsKey(value))
                {
                    diag.Warn(Path, lineNo, $"enemy '{value}' is declared twice");
                    current = lib._enemies[value];
                    continue;
                }
                current = new EnemyDef { Name = value, Line = lineNo };
                lib._enemies[value] = current;
                lib._order.Add(value);
                continue;
            }
            if (current == null)
            {
                diag.Error(Path, lineNo, $"'{key}' appears before any 'Enemy:' line");
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
                case "basic attack damage":
                case "sounds":
                case "sound":
                case "range":
                    diag.Warn(Path, lineNo,
                        $"'{current.Name}': '{key}' moved onto the enemy's cards in " +
                        $"{CardLibrary.EnemyPath} and is ignored here — delete the line");
                    break;
                // "Size: 2" is 2x2; "Size: 2 x 1" is two squares side by side
                case "size":
                {
                    var bits = value.Split('x', StringSplitOptions.RemoveEmptyEntries |
                                                StringSplitOptions.TrimEntries);
                    if (bits.Length == 1 && int.TryParse(bits[0], out int sq) && sq > 0)
                        current.SizeX = current.SizeY = sq;
                    else if (bits.Length == 2 &&
                             int.TryParse(bits[0], out int sx) && sx > 0 &&
                             int.TryParse(bits[1], out int sy) && sy > 0)
                    { current.SizeX = sx; current.SizeY = sy; }
                    else
                        diag.Error(Path, lineNo,
                            $"'{current.Name}': Size must be a number of squares, or two with an " +
                            $"x between them like '2 x 1', got '{value}'");
                    break;
                }
                case "actions":
                    if (int.TryParse(value, out int ap) && ap > 0) current.Actions = ap;
                    else diag.Error(Path, lineNo,
                        $"'{current.Name}': Actions must be a positive number of points a turn, got '{value}'");
                    break;
                case "art":
                    if (CastPlaceholder.LooksLikeAPicture(value))
                        diag.Error(Path, lineNo, $"'{current.Name}': Art is a FOLDER now, not a picture — " +
                            $"got '{value}'. Put rotations/ inside a folder and name the folder here.");
                    else current.Art = value;
                    break;
                case "colour":
                case "color":
                    if (CastPlaceholder.TryParseColour(value, out var colour)) current.Colour = colour;
                    else diag.Error(Path, lineNo,
                        $"'{current.Name}': Colour must be three numbers 0-255 like '120, 80, 40', got '{value}'");
                    break;
                case "sprites":
                    diag.Error(Path, lineNo, $"'{current.Name}': 'Sprites:' is gone — art is a folder of " +
                        "rotations now. Delete the line, or use 'Art: FolderName' to pick a folder.");
                    break;
                case "card tags":
                    current.CardTags = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    break;
                case "cast animation":
                    if (CastPlaceholder.LooksLikeAPicture(value))
                        diag.Error(Path, lineNo, $"'{current.Name}': Cast Animation names a folder under " +
                            $"animations/ now, like 'Swing' — got '{value}'");
                    else current.CastAnimation = value;
                    break;
                default:
                    diag.Warn(Path, lineNo, $"'{current.Name}': unknown line '{line}' ignored");
                    break;
            }
        }
        Current = lib;
        return lib;
    }
}
