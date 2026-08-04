using System;
using System.Collections.Generic;
using System.Linq;

namespace TheTimelineIs.Core.Data;

/// <summary>One enemy kind, from a block of Enemies.txt.</summary>
public class EnemyDef
{
    public string Name = "";
    public int Hp = 10;
    public int Movement = 3;
    public int AttackDamage = 3;
    public string? AttackSound;
    /// <summary>Attack reach in tiles; 1 = melee ("Range: Melee").</summary>
    public int Range = 1;
    public List<string> Sprites = new();
    public int Line;

    public string Folder => $"Content/Cast/EnemyCharacters/{Name}";
    public IReadOnlyList<string> SpriteFiles =>
        Sprites.Count > 0 ? Sprites : new List<string> { $"{Name}.png" };
}

/// <summary>
/// Parses Content/Cast/EnemyCharacters/Enemies.txt — one file for every enemy,
/// replacing the per-enemy manifests. Format:
///
///   Enemy: Goblin
///   HP: 30
///   Movement: 3
///   Basic Attack Damage: 5
///   Sounds: hitbasic.wav
///   Range: Melee                     (or a number of tiles)
///   Sprites: Goblin1.png, Goblin2.png, Goblin3.png
/// </summary>
public class EnemyLibrary
{
    private readonly Dictionary<string, EnemyDef> _enemies = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    public const string Path = "Content/Cast/EnemyCharacters/Enemies.txt";

    public static EnemyLibrary Current { get; private set; } = new();

    public IReadOnlyList<string> EnemyNames => _order;
    public EnemyDef? Get(string name) => _enemies.TryGetValue(name, out var e) ? e : null;

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
                    if (int.TryParse(value, out int dmg) && dmg > 0) current.AttackDamage = dmg;
                    else diag.Error(Path, lineNo, $"'{current.Name}': Basic Attack Damage must be a positive number, got '{value}'");
                    break;
                case "sounds":
                case "sound":
                    current.AttackSound = value.Trim('[', ']').Trim();
                    if (current.AttackSound.Length == 0) current.AttackSound = null;
                    break;
                case "range":
                    if (value.Equals("melee", StringComparison.OrdinalIgnoreCase)) current.Range = 1;
                    else if (int.TryParse(value, out int r) && r > 0) current.Range = r;
                    else diag.Error(Path, lineNo, $"'{current.Name}': Range must be 'Melee' or a number of tiles, got '{value}'");
                    break;
                case "sprites":
                    current.Sprites = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
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
