using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TheTimelineIs.Core.Data;

public enum CardKind
{
    AoEDamage,        // "AoE damage": hits every living enemy once
    SingleTargetHits, // "Single target, X hits.": one target, X hits of N damage
    MultiTarget,      // "Two targets, 1 hit.": pick N targets, one hit each
}

/// <summary>How the card reaches its target: [melee] walks, [ranged] throws.</summary>
public enum Delivery { Instant, Melee, Ranged }

/// <summary>One blow in a card's hit sequence: wait Delay, play Sound, deal damage.</summary>
public class HitEvent
{
    public float Delay;
    public string? Sound;
}

public class Card
{
    // --- shown to the player: case preserved exactly as authored ---
    public string Name = "";
    public string CardText = "";
    public string DamageType = "";

    // --- machine-read: matched case-insensitively ---
    public List<string> Tags = new();
    public string TypeLine = "";
    public CardKind Kind;
    public int Damage;      // per hit / per target / per enemy
    public int Hits = 1;    // SingleTargetHits only
    public int Targets = 1; // MultiTarget only

    public Delivery Delivery = Delivery.Instant;
    /// <summary>One projectile aimed at the player's pick, vs one per target.</summary>
    public bool SingleProjectile;
    public string ProjectileArt = "Projectile.png";

    public string? CastingSound;
    /// <summary>Null means "Use Sound Time" — take the casting sound's own length.</summary>
    public float? CastingTime;
    /// <summary>Feet per second, for the projectile or the melee walk.</summary>
    public float Speed = 6f;
    /// <summary>Melee only: pause on arrival before the first blow lands.</summary>
    public float MeleeTime;
    public List<HitEvent> HitEvents = new();

    /// <summary>Total damage per target, split across the hit sequence.</summary>
    public int DamagePerTarget => Kind == CardKind.SingleTargetHits ? Damage * Hits : Damage;

    /// <summary>The dynamic bottom-right number: total damage against the current room.</summary>
    public int TotalDamage(int livingEnemies) => Kind switch
    {
        CardKind.AoEDamage => Damage * livingEnemies,
        CardKind.SingleTargetHits => Damage * Hits,
        _ => Damage * Math.Min(Targets, Math.Max(1, livingEnemies)),
    };

    /// <summary>
    /// Splits this card's per-target damage across its hit events as evenly as
    /// possible, with any remainder on the last blow.
    /// </summary>
    public int[] DamageSchedule()
    {
        int events = Math.Max(1, HitEvents.Count);
        int total = DamagePerTarget;
        var schedule = new int[events];
        int each = total / events;
        for (int i = 0; i < events; i++) schedule[i] = each;
        schedule[events - 1] += total - each * events;
        return schedule;
    }
}

/// <summary>
/// Parses Content/Cast/PlayerCharacters/Cards.txt. Keys are matched
/// case-insensitively and tolerate sloppy punctuation ("Speed: 2", "Speed 2",
/// "Speed 2:" all work). Only Card Name, Card Text, and Bottom Right keep
/// their authored capitalization, since those reach the player. "[]" lines
/// mark card boundaries and are used to catch malformed blocks.
/// </summary>
public class CardLibrary
{
    public List<Card> All { get; } = new();

    public const string Path = "Content/Cast/PlayerCharacters/Cards.txt";

    // longest first, so "card text" is tested before "card"
    private static readonly string[] Keys =
    {
        "projectile art", "casting sound", "casting time", "bottom right",
        "card name", "card text", "melee time", "hit sound",
        "effect", "speed", "tags", "type", "sounds",
    };

    private static readonly Regex TrailingNote = new(@"\s*\([^()]*\)\s*$");
    private static readonly Regex Ints = new(@"\d+");
    private static readonly Regex Decimal = new(@"\d+(?:\.\d+)?");
    private static readonly Regex DeliveryTag = new(@"\[\s*(melee|ranged)\s*\]", RegexOptions.IgnoreCase);
    private static readonly Regex Bracketed = new(@"\[([^\]]*)\]");
    private static readonly Regex HitToken =
        new(@"\[(?<snd>[^\]]*)\]|delay\s*(?<d>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);

    public static CardLibrary Load()
    {
        var lib = new CardLibrary();
        Card? card = null;
        bool sawBoundary = false;

        foreach (var raw in AssetLoader.TryReadLines(Path))
        {
            string line = TrailingNote.Replace(TextUtil.Clean(raw), "").Trim();
            if (line.Length == 0) continue;

            if (line == "[]")
            {
                // a "[]" that closes a block with no Card Name means a malformed card
                if (sawBoundary && card == null)
                    Console.WriteLine("[cards] a [] block contained no 'Card Name:' line");
                sawBoundary = !sawBoundary;
                if (!sawBoundary) card = null;
                continue;
            }

            string? key = Keys.FirstOrDefault(k =>
                line.StartsWith(k, StringComparison.OrdinalIgnoreCase));
            if (key == null)
            {
                Console.WriteLine($"[cards] unrecognized line ignored: {line}");
                continue;
            }
            string value = line[key.Length..].TrimStart(' ', ':', '\t').Trim();

            if (key == "card name")
            {
                card = new Card { Name = value };
                lib.All.Add(card);
                continue;
            }
            if (card == null)
            {
                Console.WriteLine($"[cards] line before any 'Card Name:' ignored: {line}");
                continue;
            }
            Apply(card, key, value);
        }

        foreach (var c in lib.All) Validate(c);
        return lib;
    }

    private static void Apply(Card card, string key, string value)
    {
        switch (key)
        {
            case "tags":
                card.Tags = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                break;

            case "type":
                var delivery = DeliveryTag.Match(value);
                if (delivery.Success)
                {
                    card.Delivery = delivery.Groups[1].Value.Equals("melee", StringComparison.OrdinalIgnoreCase)
                        ? Delivery.Melee : Delivery.Ranged;
                    value = DeliveryTag.Replace(value, "").Trim();
                }
                // "Single Projectile" draws one shot at the player's pick;
                // "Multiple Projectiles" draws one per target (the default).
                card.SingleProjectile = value.Contains("single projectile", StringComparison.OrdinalIgnoreCase);
                card.TypeLine = value;
                break;

            case "projectile art":
                if (value.Length > 0) card.ProjectileArt = Unbracket(value);
                break;

            case "casting sound":
                card.CastingSound = SoundOrNull(value);
                break;

            case "casting time":
                // "Use Sound Time" (the default) leaves this null
                card.CastingTime = value.Contains("sound", StringComparison.OrdinalIgnoreCase)
                    ? null : ParseFloat(value);
                break;

            case "speed":
                card.Speed = ParseFloat(value) is float s && s > 0 ? s : card.Speed;
                break;

            case "melee time":
                card.MeleeTime = ParseFloat(value) ?? 0f;
                break;

            case "hit sound":
                card.HitEvents = ParseHitSequence(value);
                break;

            case "effect":
                ApplyEffect(card, value);
                break;

            case "card text":
                card.CardText = value;
                break;

            case "bottom right":
                // "15 Fire" -> damage type "Fire"; the number is recomputed live
                card.DamageType = Regex.Replace(value, @"^\s*\d+\s*", "");
                break;

            case "sounds":
                Console.WriteLine($"[cards] '{card.Name}': the old 'Sounds:' line is obsolete — " +
                    "use Casting Sound / Casting Time / Hit Sound. Ignored.");
                break;
        }
    }

    private static string Unbracket(string v)
    {
        var m = Bracketed.Match(v);
        return (m.Success ? m.Groups[1].Value : v).Trim();
    }

    /// <summary>"[Blank]" and "[Blank.wav]" mean no sound and no delay.</summary>
    private static string? SoundOrNull(string value)
    {
        string name = Unbracket(value);
        if (name.Length == 0) return null;
        string bare = System.IO.Path.GetFileNameWithoutExtension(name);
        return bare.Equals("blank", StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    private static float? ParseFloat(string value)
    {
        var m = Decimal.Match(value);
        return m.Success && float.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
            ? f : null;
    }

    /// <summary>
    /// "[a.wav], Delay 0.2, [b.wav]" — bracketed names are blows, "Delay N"
    /// sets the gap before the next one. The health drop is timed to each blow.
    /// </summary>
    private static List<HitEvent> ParseHitSequence(string value)
    {
        var events = new List<HitEvent>();
        float pendingDelay = 0f;
        foreach (Match m in HitToken.Matches(value))
        {
            if (m.Groups["d"].Success)
            {
                if (float.TryParse(m.Groups["d"].Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float d))
                    pendingDelay = d;
            }
            else
            {
                string name = m.Groups["snd"].Value.Trim();
                string bare = System.IO.Path.GetFileNameWithoutExtension(name);
                events.Add(new HitEvent
                {
                    Delay = pendingDelay,
                    Sound = bare.Equals("blank", StringComparison.OrdinalIgnoreCase) || name.Length == 0
                        ? null : name,
                });
                pendingDelay = 0f;
            }
        }
        if (events.Count == 0) events.Add(new HitEvent());
        return events;
    }

    private static void ApplyEffect(Card card, string effect)
    {
        var nums = Ints.Matches(effect).Select(m => int.Parse(m.Value)).ToList();
        string type = card.TypeLine.ToLowerInvariant();

        if (type.Contains("aoe"))
        {
            card.Kind = CardKind.AoEDamage;
            card.Damage = nums.Count > 0 ? nums[0] : 0;
        }
        else if (type.Contains("single target"))
        {
            // "Single target. 3 times x 4 damage." -> hits 3, damage 4
            card.Kind = CardKind.SingleTargetHits;
            card.Hits = nums.Count > 0 ? nums[0] : 1;
            card.Damage = nums.Count > 1 ? nums[1] : (nums.Count > 0 ? nums[0] : 0);
        }
        else if (type.Contains("target"))
        {
            card.Kind = CardKind.MultiTarget;
            card.Damage = nums.Count > 0 ? nums[0] : 0;
            card.Targets = type.Contains("two") ? 2 : type.Contains("three") ? 3
                : type.Contains("four") ? 4 : 2;
        }
        else
        {
            Console.WriteLine($"[cards] '{card.Name}' has unrecognized Type '{card.TypeLine}'; treating as single hit");
            card.Kind = CardKind.SingleTargetHits;
            card.Damage = nums.Count > 0 ? nums[0] : 0;
        }
    }

    private static void Validate(Card c)
    {
        if (c.Damage <= 0)
            Console.WriteLine($"[cards] '{c.Name}' has no parsable damage in its Effect line");
        if (c.Tags.Count == 0)
            Console.WriteLine($"[cards] '{c.Name}' has no Tags — no class can play it");
        if (c.Kind == CardKind.SingleTargetHits && c.HitEvents.Count > 1 && c.HitEvents.Count != c.Hits)
            Console.WriteLine($"[cards] '{c.Name}': {c.Hits} hits in Effect but {c.HitEvents.Count} " +
                "Hit Sound entries — damage is split across the sounds instead");
    }

    /// <summary>Cards visible to a class, matched on shared tags.</summary>
    public List<Card> HandFor(IReadOnlyList<string> classTags) =>
        All.Where(c => c.Tags.Intersect(classTags, StringComparer.OrdinalIgnoreCase).Any()).ToList();
}
