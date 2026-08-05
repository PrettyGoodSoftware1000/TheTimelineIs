using System;
using System.Collections.Generic;
using System.Linq;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// A concrete character on stage during a mission run. The Nth mention of a
/// name in a room's Cast line maps to the Nth instance of that character in
/// the run, so instances keep their sprite and alive/dead state across rooms.
/// </summary>
public class CharacterInstance
{
    public string Name = "";
    public int OccurrenceIndex;      // 0 = first Goblin in a cast line, 1 = second...
    public string SpriteFile = "";   // e.g. "Goblin2.png"
    public bool IsPlayer;
    public bool Alive = true;

    public int MaxHp = 20;
    public int Hp = 20;
    public int AttackDmg;            // enemies auto-attack with this; players use cards
    public string AttackType = "Hit";
    /// <summary>0 = Back, 1 = Mid, 2 = Front. -1 = not yet placed.</summary>
    public int Row = -1;

    // --- transient render state: never saved, reset every battle ---

    /// <summary>Melee walk displacement, in virtual px.</summary>
    public Microsoft.Xna.Framework.Vector2 WalkOffset;

    /// <summary>Counts down while the sprite is recoiling from a hit.</summary>
    public float ShakeTimer;

    // --- isometric grid state ---
    public int GX, GY;
    /// <summary>Movement points per turn (from Classes.txt / Enemies.txt).</summary>
    public int MoveMax = 5;
    /// <summary>Points left this turn.</summary>
    public int MovePoints;
    /// <summary>Enemy basic attack reach in tiles; players attack via cards.</summary>
    public int RangeTiles = 1;
    public string? AttackSound;

    // --- status effects ---
    /// <summary>
    /// One entry per stack of Burning, holding that stack's own remaining
    /// turns. Stacks are independent: a stack applied later expires later,
    /// and adding one never extends the ones already burning.
    /// </summary>
    public List<int> Burns = new();

    /// <summary>Live stacks; each deals Effects.BurnDamagePerStack at this character's turn start.</summary>
    public int BurningStacks => Burns.Count;

    /// <summary>Soaks damage before health does, and shows grey on the bar.</summary>
    public int Armor;

    public string Folder => IsPlayer
        ? $"Content/Cast/PlayerCharacters/{Name}"
        : $"Content/Cast/EnemyCharacters/{Name}";
    public string SpritePath => $"{Folder}/{SpriteFile}";
    /// <summary>Optional: Goblin1.png -> Goblin1Thumb.png. Falls back to the full sprite.</summary>
    public string ThumbPath => $"{Folder}/{System.IO.Path.GetFileNameWithoutExtension(SpriteFile)}Thumb.png";

    public CharacterInstance Clone()
    {
        var copy = (CharacterInstance)MemberwiseClone();
        copy.Burns = new List<int>(Burns);   // don't share the stack list with the original
        return copy;
    }
}

/// <summary>
/// Compatibility view over Classes.txt and Enemies.txt for the older
/// side-view screens: the per-character {Name}.txt manifests are gone, so
/// this now just adapts the two libraries into the shape those screens read.
/// </summary>
public class CastManifest
{
    public string Name = "";
    public bool IsPlayer;
    public List<string> Variants = new();
    public int Hp = -1;
    public int AttackDmg = -1;
    public string AttackType = "Hit";

    public static CastManifest Get(string name)
    {
        if (ClassLibrary.Current.Get(name) is PlayerClass cls)
            return new CastManifest
            {
                Name = cls.Name,
                IsPlayer = true,
                Variants = cls.SpriteFiles.ToList(),
                Hp = cls.Hp,
            };
        if (EnemyLibrary.Current.Get(name) is EnemyDef enemy)
            return new CastManifest
            {
                Name = enemy.Name,
                IsPlayer = false,
                Variants = enemy.SpriteFiles.ToList(),
                Hp = enemy.Hp,
                AttackDmg = enemy.AttackDamage,
            };
        Diagnostics.Current.Error($"Cast/{name}", 0,
            $"'{name}' is in neither {ClassLibrary.Path} nor {EnemyLibrary.Path}");
        return new CastManifest { Name = name, Variants = { $"{name}.png" } };
    }
}

public static class CastResolver
{
    private static readonly Random Rng = new();

    /// <summary>
    /// Resolve a room's Cast line against the instances already in the run.
    /// Existing alive instances return with their sprites intact; dead ones
    /// are omitted (the file assumes everyone lived — reality may differ);
    /// unseen occurrence indices spawn new instances, each preferring a
    /// sprite variant no other instance is using.
    /// </summary>
    public static List<CharacterInstance> EnterRoom(List<CharacterInstance> runInstances, List<string> cast)
    {
        var present = new List<CharacterInstance>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in cast)
        {
            int occ = seen.TryGetValue(name, out int n) ? n : 0;
            seen[name] = occ + 1;

            var existing = runInstances.FirstOrDefault(i =>
                i.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && i.OccurrenceIndex == occ);
            if (existing != null)
            {
                if (existing.Alive)
                    present.Add(existing);
                continue; // dead: omitted, not backfilled
            }

            var manifest = CastManifest.Get(name);
            int maxHp = manifest.Hp > 0 ? manifest.Hp : (manifest.IsPlayer ? 25 : 12);
            var inst = new CharacterInstance
            {
                Name = manifest.Name,
                OccurrenceIndex = occ,
                IsPlayer = manifest.IsPlayer,
                SpriteFile = PickVariant(manifest, runInstances),
                MaxHp = maxHp,
                Hp = maxHp,
                AttackDmg = manifest.AttackDmg > 0 ? manifest.AttackDmg : (manifest.IsPlayer ? 0 : 3),
                AttackType = manifest.AttackType,
            };
            runInstances.Add(inst);
            present.Add(inst);
        }
        return present;
    }

    /// <summary>Least-used variant wins; random among ties. Unique while supply lasts.</summary>
    private static string PickVariant(CastManifest manifest, List<CharacterInstance> runInstances)
    {
        var usage = manifest.Variants.ToDictionary(v => v, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var inst in runInstances)
            if (inst.Name.Equals(manifest.Name, StringComparison.OrdinalIgnoreCase) &&
                usage.ContainsKey(inst.SpriteFile))
                usage[inst.SpriteFile]++;

        int min = usage.Values.Min();
        var candidates = usage.Where(kv => kv.Value == min).Select(kv => kv.Key).ToList();
        return candidates[Rng.Next(candidates.Count)];
    }
}
