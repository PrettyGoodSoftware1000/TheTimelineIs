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

    public string Folder => IsPlayer
        ? $"Content/cast/player_characters/{Name}"
        : $"Content/cast/enemy_characters/{Name}";
    public string SpritePath => $"{Folder}/{SpriteFile}";
    public string ThumbPath => $"{Folder}/{System.IO.Path.GetFileNameWithoutExtension(SpriteFile)}_thumb.png";

    public CharacterInstance Clone() => (CharacterInstance)MemberwiseClone();
}

/// <summary>
/// Character folders each carry a manifest ({Name}.txt listing sprite files)
/// because a mobile app bundle can't enumerate directories — the manifest is
/// how the game knows Goblin has three images.
/// </summary>
public class CastManifest
{
    public string Name = "";
    public bool IsPlayer;
    public List<string> Variants = new();

    private static readonly Dictionary<string, CastManifest> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static CastManifest Get(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;

        var manifest = new CastManifest { Name = name };
        var lines = AssetLoader.TryReadLines($"Content/cast/player_characters/{name}/{name}.txt");
        if (lines.Count > 0)
        {
            manifest.IsPlayer = true;
        }
        else
        {
            lines = AssetLoader.TryReadLines($"Content/cast/enemy_characters/{name}/{name}.txt");
            if (lines.Count == 0)
            {
                Console.WriteLine($"[cast] no manifest found for '{name}' — expected " +
                    $"Content/cast/player_characters/{name}/{name}.txt or Content/cast/enemy_characters/{name}/{name}.txt");
                lines = new List<string> { $"{name}.png" };
            }
        }
        manifest.Variants = lines;
        Cache[name] = manifest;
        return manifest;
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
            var inst = new CharacterInstance
            {
                Name = manifest.Name,
                OccurrenceIndex = occ,
                IsPlayer = manifest.IsPlayer,
                SpriteFile = PickVariant(manifest, runInstances),
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
