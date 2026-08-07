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

/// <summary>
/// How the card reaches its target: [melee] walks in, [ranged] throws a
/// projectile, [cone] sprays a widening wedge from the caster.
/// </summary>
public enum Delivery { Instant, Melee, Ranged, Cone }

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

    /// <summary>The file this card was read from — PlayerCards.txt or EnemyCards.txt.</summary>
    public string Source = CardLibrary.PlayerPath;

    /// <summary>
    /// Action points this card costs to play. Everyone gets 2 a turn, so the
    /// default of 1 means two cards a turn; 0 makes a card free.
    /// </summary>
    public int ActionCost = 1;
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
    /// <summary>
    /// Blast radius in tiles around the impact point, for AoE cards. Separate
    /// from Range, which is only how far the card can be thrown.
    /// </summary>
    public int ExplosionRange;
    public List<HitEvent> HitEvents = new();

    /// <summary>Effects this card applies, e.g. Burning 1, Armor 5, Form Witch.</summary>
    public List<CardEffect> Effects = new();

    /// <summary>
    /// Only playable while its owner is in this form; blank means any form.
    /// Forms are declared per class in Classes.txt.
    /// </summary>
    public string Form = "";

    /// <summary>Cone and blast cards paint an area, so they can be aimed at bare ground.</summary>
    public bool TargetsGround => Kind == CardKind.AoEDamage || Delivery == Delivery.Cone;

    /// <summary>A card that only helps (Armor, no damage) is aimed at the party instead.</summary>
    public bool TargetsAllies => Damage <= 0 && Effects.Exists(e => Effects_IsFriendly(e));

    /// <summary>
    /// Cards that do not care whose side the target is on. Stealing works the
    /// same on a friend as on a foe, so it may be pointed at either.
    /// </summary>
    public bool TargetsAnyone => Effects.Exists(e => e.Is(Data.Effects.Steal));

    private static bool Effects_IsFriendly(CardEffect e) => Data.Effects.IsFriendly(e.Name);

    public CardEffect? EffectNamed(string name) => Effects.Find(e => e.Is(name));

    /// <summary>Extra approach range this card grants while closing in (Leap).</summary>
    public int LeapBonus => EffectNamed(Data.Effects.Leap)?.Amount ?? 0;

    /// <summary>Leaping ignores how far up or down the ground goes.</summary>
    public bool IgnoresHeight => LeapBonus > 0;

    /// <summary>The form this card changes its caster into, if any.</summary>
    public string? BecomesForm => EffectNamed(Data.Effects.Form)?.Text is { Length: > 0 } f ? f : null;

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

    /// <summary>Cards the party can play.</summary>
    public const string PlayerPath = "Content/Cards/PlayerCards.txt";

    /// <summary>Cards enemies act with. Same format, different holders.</summary>
    public const string EnemyPath = "Content/Cards/EnemyCards.txt";

    /// <summary>
    /// Reserved tag: a card tagged with this is dealt to any enemy that has no
    /// card of its own, so a newly added enemy can still fight.
    /// </summary>
    public const string DefaultTag = "Default";

    /// <summary>Which file this library was read from — for error messages.</summary>
    public string Source { get; private set; } = PlayerPath;

    // longest first, so "card text" is tested before "card"
    private static readonly string[] Keys =
    {
        "projectile art", "casting sound", "casting time", "bottom right",
        "card name", "card text", "melee time", "hit sound",
        "explosion range", "action points", "effects", "effect", "speed", "range",
        "form", "tags", "type", "sounds",
    };

    private static readonly Regex TrailingNote = new(@"\s*\([^()]*\)\s*$");
    private static readonly Regex Ints = new(@"\d+");
    private static readonly Regex Decimal = new(@"\d+(?:\.\d+)?");
    private static readonly Regex DeliveryTag = new(@"\[\s*(melee|ranged|cone)\s*\]", RegexOptions.IgnoreCase);
    private static readonly Regex Bracketed = new(@"\[([^\]]*)\]");
    private static readonly Regex HitToken =
        new(@"\[(?<snd>[^\]]*)\]|delay\s*(?<d>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);

    public static CardLibrary Load(string path)
    {
        var diag = Diagnostics.Current;
        var lib = new CardLibrary { Source = path };
        Card? card = null;
        bool inBlock = false;
        int blockLine = 0;

        foreach (var (lineNo, raw) in AssetLoader.ReadNumbered(path, path))
        {
            string line = TrailingNote.Replace(TextUtil.Clean(raw), "").Trim();
            if (line.Length == 0) continue;

            if (line == "[]")
            {
                if (inBlock && card == null)
                    diag.Error(path, blockLine, "this [] block has no 'Card Name:' line");
                inBlock = !inBlock;
                blockLine = lineNo;
                if (!inBlock) card = null;
                continue;
            }

            string? key = Keys.FirstOrDefault(k =>
                line.StartsWith(k, StringComparison.OrdinalIgnoreCase));
            if (key == null)
            {
                diag.Error(path, lineNo,
                    $"unrecognized line '{Trim(line)}' — expected one of: {string.Join(", ", Keys)}");
                continue;
            }
            string value = line[key.Length..].TrimStart(' ', ':', '\t').Trim();

            if (key == "card name")
            {
                if (value.Length == 0)
                {
                    diag.Error(path, lineNo, "'Card Name:' has no name after it");
                    continue;
                }
                if (lib.All.Any(c => c.Name.Equals(value, StringComparison.OrdinalIgnoreCase)))
                    diag.Error(path, lineNo, $"a second card is also named '{value}'");
                card = new Card { Name = value, Line = lineNo, Source = path };
                lib.All.Add(card);
                continue;
            }
            if (card == null)
            {
                diag.Error(path, lineNo, $"'{key}' appears before any 'Card Name:' line");
                continue;
            }
            Apply(card, key, value, lineNo, diag);
        }

        if (inBlock)
            diag.Error(path, blockLine, "a [] block was opened but never closed");
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
                    card.Delivery = delivery.Groups[1].Value.ToLowerInvariant() switch
                    {
                        "melee" => Delivery.Melee,
                        "cone" => Delivery.Cone,
                        _ => Delivery.Ranged,
                    };
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
                    diag.Error(card.Source, lineNo,
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
                    diag.Error(card.Source, lineNo,
                        $"'{card.Name}': Speed must be a number of feet per second " +
                        $"(or 0 to use the default), got '{value}'");
                }
                break;

            case "range":
                if (int.TryParse(value, out int rng) && rng > 0) card.Range = rng;
                else diag.Error(card.Source, lineNo,
                    $"'{card.Name}': Range must be a positive number of tiles, got '{value}'");
                break;

            case "effects":
                ApplyEffects(card, value, lineNo, diag);
                break;

            case "form":
                card.Form = value;
                break;

            case "action points":
                // 0 is a real value here: a free card that costs nothing to play
                if (int.TryParse(value, out int ap) && ap >= 0) card.ActionCost = ap;
                else diag.Error(card.Source, lineNo,
                    $"'{card.Name}': Action Points must be 0 or more, got '{value}'");
                break;

            case "explosion range":
                if (int.TryParse(value, out int blast) && blast > 0) card.ExplosionRange = blast;
                else diag.Error(card.Source, lineNo,
                    $"'{card.Name}': Explosion Range must be a positive number of tiles, got '{value}'");
                break;

            case "melee time":
                if (ParseFloat(value) is float mt) card.MeleeTime = mt;
                else diag.Error(card.Source, lineNo,
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
                diag.Warn(card.Source, lineNo, $"'{card.Name}': the old 'Sounds:' line is " +
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

    /// <summary>"Burning 1, Armor 5" — each entry is a name and an optional amount (default 1).</summary>
    private static void ApplyEffects(Card card, string value, int lineNo, Diagnostics diag)
    {
        card.Effects = new List<CardEffect>();
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string entry = raw.Replace("[", " ").Replace("]", " ").Trim();
            // the effect name leads; whatever follows is a number, or a word for Form
            string? name = Data.Effects.Known.FirstOrDefault(k =>
                entry.StartsWith(k, StringComparison.OrdinalIgnoreCase));
            if (name == null)
            {
                diag.Error(card.Source, lineNo,
                    $"'{card.Name}': unknown effect '{raw}' — known effects are " +
                    string.Join(", ", Data.Effects.Known));
                continue;
            }
            string rest = entry[name.Length..].Trim();
            if (Data.Effects.TakesText(name))
            {
                if (rest.Length == 0)
                    diag.Error(card.Source, lineNo, $"'{card.Name}': '{name}' needs a name after it");
                else
                    card.Effects.Add(new CardEffect(name, 1, rest));
                continue;
            }
            if (rest.Length == 0) card.Effects.Add(new CardEffect(name, 1));
            else if (int.TryParse(rest, out int amount)) card.Effects.Add(new CardEffect(name, amount));
            else diag.Error(card.Source, lineNo,
                $"'{card.Name}': effect '{name}' wants a number, got '{rest}'");
        }
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
            diag.Error(card.Source, lineNo, $"'{card.Name}': unrecognized Type '{card.TypeLine}' — " +
                "expected 'AoE damage', 'Single target, X hits.' or 'N targets, 1 hit.'; treated as a single hit");
            card.Kind = CardKind.SingleTargetHits;
            card.Damage = nums.Count > 0 ? nums[0] : 0;
        }
    }

    private static void Validate(Card c, Diagnostics diag)
    {
        if (c.Range <= 0)
            c.Range = c.Delivery == Delivery.Melee ? 1 : 5;   // melee reaches 1 tile; ranged defaults to 5
        if (c.Kind == CardKind.AoEDamage && c.Delivery != Delivery.Cone && c.ExplosionRange <= 0)
        {
            c.ExplosionRange = 1;
            diag.Warn(c.Source, c.Line, $"'{c.Name}': AoE card has no 'Explosion Range:' line; " +
                "using 1 tile. Range is only how far it can be thrown.");
        }

        if (c.Damage <= 0 && c.Effects.Count == 0)
            diag.Error(c.Source, c.Line,
                $"'{c.Name}': no damage number in its Effect line, and no Effects to apply either");
        if (c.Tags.Count == 0)
            diag.Error(c.Source, c.Line, $"'{c.Name}': no Tags, so no class can ever play it");
        if (c.CardText.Length == 0)
            diag.Warn(c.Source, c.Line, $"'{c.Name}': no Card Text, so the card face is blank");
        if (c.DamageType.Length == 0)
            diag.Warn(c.Source, c.Line, $"'{c.Name}': no damage type in Bottom Right");
        if (c.Kind == CardKind.SingleTargetHits && c.HitEvents.Count > 1 && c.HitEvents.Count != c.Hits)
            diag.Warn(c.Source, c.Line, $"'{c.Name}': Effect says {c.Hits} hits but there are " +
                $"{c.HitEvents.Count} Hit Sound entries — damage is split across the sounds instead");
        if (c.Delivery == Delivery.Instant && (c.HitEvents.Count > 1 || c.MeleeTime > 0))
            diag.Warn(c.Source, c.Line, $"'{c.Name}': has timing set but no [melee] or [ranged] tag, " +
                "so it resolves instantly");
    }

    /// <summary>Cards a class can play: any tag the class carries appears in the card's Tags.</summary>
    public List<Card> HandFor(IReadOnlyList<string> classTags) =>
        All.Where(c => c.Tags.Intersect(classTags, StringComparer.OrdinalIgnoreCase).Any()).ToList();

    /// <summary>The cards dealt to a holder that has none of its own.</summary>
    public List<Card> DefaultHand() =>
        All.Where(c => c.Tags.Contains(DefaultTag, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The same, narrowed to a character's current form: a card with a Form
    /// line only shows while its owner wears that form, so the Werewitch's
    /// claws and curses never sit in the hand together.
    /// </summary>
    public List<Card> HandFor(IReadOnlyList<string> classTags, string form) =>
        HandFor(classTags)
            .Where(c => c.Form.Length == 0 || c.Form.Equals(form, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
