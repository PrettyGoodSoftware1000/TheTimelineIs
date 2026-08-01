using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Data;

public enum CardKind
{
    AoEDamage,        // "AoE damage": hits every living enemy once
    SingleTargetHits, // "Single target, X hits.": one target, X hits of N damage
    MultiTarget,      // "Two targets, 1 hit.": pick N targets, one hit each
}

/// <summary>How the card reaches its target: [melee] walks, [ranged] throws.</summary>
public enum Delivery { Instant, Melee, Ranged }

public class Card
{
    public string Name = "";
    public List<string> Tags = new();
    public string TypeLine = "";
    public string CardText = "";
    public string DamageType = "";
    public CardKind Kind;
    public int Damage;      // per hit / per target / per enemy
    public int Hits = 1;    // SingleTargetHits only
    public int Targets = 1; // MultiTarget only

    public Delivery Delivery = Delivery.Instant;

    // "Sounds: Activation[cast.wav], 0.6, Hit[boom.wav]"
    public string? ActivationSound;
    public string? HitSound;
    /// <summary>Seconds for the walk or projectile to reach the target before the hit lands.</summary>
    public float TravelSeconds;

    /// <summary>The dynamic bottom-right number: total damage against the current room.</summary>
    public int TotalDamage(int livingEnemies) => Kind switch
    {
        CardKind.AoEDamage => Damage * livingEnemies,
        CardKind.SingleTargetHits => Damage * Hits,
        _ => Damage * Math.Min(Targets, Math.Max(1, livingEnemies)),
    };
}

/// <summary>
/// Parses Content/Cast/PlayerCharacters/Cards.txt. "Card Name:" starts a card;
/// Tags/Type/Effect/Card Text/Bottom Right fill it in. A trailing "(...)" on
/// any line is treated as an author note and stripped. The Effect line is the
/// machine-readable source of numbers; Card Text is what the player reads.
/// </summary>
public class CardLibrary
{
    public List<Card> All { get; } = new();

    public const string Path = "Content/Cast/PlayerCharacters/Cards.txt";

    private static readonly Regex TrailingNote = new(@"\s*\([^()]*\)\s*$");
    private static readonly Regex Ints = new(@"\d+");
    private static readonly Regex DeliveryTag = new(@"\[\s*(melee|ranged)\s*\]", RegexOptions.IgnoreCase);
    private static readonly Regex SoundTag = new(@"(activation|hit)\s*\[\s*([^\]]+?)\s*\]", RegexOptions.IgnoreCase);
    private static readonly Regex Seconds = new(@"(?<![\[\w.])(\d+(?:\.\d+)?)(?![\w.])");

    public static CardLibrary Load()
    {
        var lib = new CardLibrary();
        Card? card = null;
        foreach (var raw in AssetLoader.TryReadLines(Path))
        {
            string line = TrailingNote.Replace(TextUtil.Clean(raw), "");
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string key = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();

            if (key == "card name")
            {
                card = new Card { Name = value };
                lib.All.Add(card);
                continue;
            }
            if (card == null) continue;
            switch (key)
            {
                case "tags":
                    card.Tags = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    break;
                case "type":
                    // an optional [melee] / [ranged] tag leads the type line
                    var delivery = DeliveryTag.Match(value);
                    if (delivery.Success)
                    {
                        card.Delivery = delivery.Groups[1].Value.Equals("melee", StringComparison.OrdinalIgnoreCase)
                            ? Delivery.Melee : Delivery.Ranged;
                        value = DeliveryTag.Replace(value, "").Trim();
                    }
                    card.TypeLine = value;
                    break;
                case "sounds":
                    ApplySounds(card, value);
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
                default:
                    Console.WriteLine($"[cards] unknown line on '{card.Name}' ignored: {raw}");
                    break;
            }
        }
        foreach (var c in lib.All)
            if (c.Damage <= 0)
                Console.WriteLine($"[cards] '{c.Name}' has no parsable damage in its Effect line");
        return lib;
    }

    /// <summary>
    /// "Activation[cast.wav], 0.6, Hit[boom.wav]" — file names live inside the
    /// brackets; the bare number is the travel time in seconds before the hit
    /// lands. Any part may be omitted.
    /// </summary>
    private static void ApplySounds(Card card, string value)
    {
        foreach (Match m in SoundTag.Matches(value))
        {
            if (m.Groups[1].Value.Equals("activation", StringComparison.OrdinalIgnoreCase))
                card.ActivationSound = m.Groups[2].Value;
            else
                card.HitSound = m.Groups[2].Value;
        }
        // read the delay from whatever is left once the bracketed names are gone
        var seconds = Seconds.Match(SoundTag.Replace(value, ""));
        if (seconds.Success &&
            float.TryParse(seconds.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float s))
            card.TravelSeconds = MathHelper.Clamp(s, 0f, 10f);
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

    /// <summary>Cards visible to a class, matched on shared tags.</summary>
    public List<Card> HandFor(IReadOnlyList<string> classTags) =>
        All.Where(c => c.Tags.Intersect(classTags, StringComparer.OrdinalIgnoreCase).Any()).ToList();
}
