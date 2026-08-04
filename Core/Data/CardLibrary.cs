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

    /// <summary>Line in Cards.txt where this card starts, for error messages.</summary>
    public int Line;
    public string EffectLine = "";

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
    /// <summary>
    /// Seconds to wait before launching. Null means "Use Sound Time" — the
    /// casting sound's own length, which is 0 when there is no sound.
    /// </summary>
    public float? CastingTime;
    /// <summary>Feet per second, for the projectile or the melee walk. 0 in the file = use this default.</summary>
    public float Speed = 6f;
    /// <summary>Melee only: pause on arrival before the first blow lands.</summary>
    public float MeleeTime;
    /// <summary>Attack reach in tiles (isometric mode): melee 1, ranged default 5. Diagonals cost 2.</summary>
    public int Range;
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
        "effect", "speed", "range", "tags", "type", "sounds",
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
        var diag = Diagnostics.Current;
        var lib = new CardLibrary();
        Card? card = null;
        bool inBlock = false;
        int blockLine = 0;

        foreach (var (lineNo, raw) in AssetLoader.ReadNumbered(Path, Path))
        {
            string line = TrailingNote.Replace(TextUtil.Clean(raw), "").Trim();
            if (line.Length == 0) continue;

            if (line == "[]")
            {
                if (inBlock && card == null)
                    diag.Error(Path, blockLine, "this [] block has no 'Card Name:' line");
                inBlock = !inBlock;
                blockLine = lineNo;
                if (!inBlock) card = null;
                continue;
            }

            string? key = Keys.FirstOrDefault(k =>
                line.StartsWith(k, StringComparison.OrdinalIgnoreCase));
            if (key == null)
            {
                diag.Error(Path, lineNo,
                    $"unrecognized line '{Trim(line)}' — expected one of: {string.Join(", ", Keys)}");
                continue;
            }
            string value = line[key.Length..].TrimStart(' ', ':', '\t').Trim();

            if (key == "card name")
            {
                if (value.Length == 0)
                {
                    diag.Error(Path, lineNo, "'Card Name:' has no name after it");
                    continue;
                }
                if (lib.All.Any(c => c.Name.Equals(value, StringComparison.OrdinalIgnoreCase)))
                    diag.Error(Path, lineNo, $"a second card is also named '{value}'");
                card = new Card { Name = value, Line = lineNo };
                lib.All.Add(card);
                continue;
            }
            if (card == null)
            {
                diag.Error(Path, lineNo, $"'{key}' appears before any 'Card Name:' line");
                continue;
            }
            Apply(card, key, value, lineNo, diag);
        }

        if (inBlock)
            diag.Error(Path, blockLine, "a [] block was opened but never closed");
        foreach (var c in lib.All) Validate(c, diag);
        return lib;
    }

    private static string Trim(string s) => s.Length <= 60 ? s : s[..57] + "...";

    private static void Apply(Card card, string key, string value, int lineNo, Diagnostics diag)
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
                if (value.Contains("sound", StringComparison.OrdinalIgnoreCase))
                    card.CastingTime = null;
                else if (ParseFloat(value) is float ct)
                    card.CastingTime = ct;
                else
                    diag.Error(CardLibrary.Path, lineNo,
                        $"'{card.Name}': Casting Time must be a number or 'Use Sound Time', got '{value}'");
                break;

            case "speed":
                // 0 means "ignore this line", same as everywhere else
                if (ParseFloat(value) is float sp)
                {
                    if (sp > 0) card.Speed = sp;
                }
                else
                {
                    diag.Error(CardLibrary.Path, lineNo,
                        $"'{card.Name}': Speed must be a number of feet per second " +
                        $"(or 0 to use the default), got '{value}'");
                }
                break;

            case "range":
                if (int.TryParse(value, out int rng) && rng > 0) card.Range = rng;
                else diag.Error(CardLibrary.Path, lineNo,
                    $"'{card.Name}': Range must be a positive number of tiles, got '{value}'");
                break;

            case "melee time":
                if (ParseFloat(value) is float mt) card.MeleeTime = mt;
                else diag.Error(CardLibrary.Path, lineNo,
                    $"'{card.Name}': Melee Time must be a number of seconds, got '{value}'");
                break;

            case "hit sound":
                card.HitEvents = ParseHitSequence(value);
                break;

            case "effect":
                card.EffectLine = value;
                ApplyEffect(card, value, lineNo, diag);
                break;

            case "card text":
                card.CardText = value;
                break;

            case "bottom right":
                // "15 Fire" -> damage type "Fire"; the number is recomputed live
                card.DamageType = Regex.Replace(value, @"^\s*\d+\s*", "");
                break;

            case "sounds":
                diag.Warn(CardLibrary.Path, lineNo, $"'{card.Name}': the old 'Sounds:' line is " +
                    "obsolete — use Casting Sound / Casting Time / Hit Sound. Ignored.");
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

    private static void ApplyEffect(Card card, string effect, int lineNo, Diagnostics diag)
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
            diag.Error(CardLibrary.Path, lineNo, $"'{card.Name}': unrecognized Type '{card.TypeLine}' — " +
                "expected 'AoE damage', 'Single target, X hits.' or 'N targets, 1 hit.'; treated as a single hit");
            card.Kind = CardKind.SingleTargetHits;
            card.Damage = nums.Count > 0 ? nums[0] : 0;
        }
    }

    private static void Validate(Card c, Diagnostics diag)
    {
        if (c.Range <= 0)
            c.Range = c.Delivery == Delivery.Melee ? 1 : 5;   // melee reaches 1 tile; ranged defaults to 5

        if (c.Damage <= 0)
            diag.Error(Path, c.Line, $"'{c.Name}': no damage number found in its Effect line");
        if (c.Tags.Count == 0)
            diag.Error(Path, c.Line, $"'{c.Name}': no Tags, so no class can ever play it");
        if (c.CardText.Length == 0)
            diag.Warn(Path, c.Line, $"'{c.Name}': no Card Text, so the card face is blank");
        if (c.DamageType.Length == 0)
            diag.Warn(Path, c.Line, $"'{c.Name}': no damage type in Bottom Right");
        if (c.Kind == CardKind.SingleTargetHits && c.HitEvents.Count > 1 && c.HitEvents.Count != c.Hits)
            diag.Warn(Path, c.Line, $"'{c.Name}': Effect says {c.Hits} hits but there are " +
                $"{c.HitEvents.Count} Hit Sound entries — damage is split across the sounds instead");
        if (c.Delivery == Delivery.Instant && (c.HitEvents.Count > 1 || c.MeleeTime > 0))
            diag.Warn(Path, c.Line, $"'{c.Name}': has timing set but no [melee] or [ranged] tag, " +
                "so it resolves instantly");
    }

    /// <summary>Cards a class can play: any tag the class carries appears in the card's Tags.</summary>
    public List<Card> HandFor(IReadOnlyList<string> classTags) =>
        All.Where(c => c.Tags.Intersect(classTags, StringComparer.OrdinalIgnoreCase).Any()).ToList();
}
