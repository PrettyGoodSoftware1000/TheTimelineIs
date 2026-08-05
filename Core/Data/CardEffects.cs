using System;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// One effect a card applies, e.g. "Burning 1" or "Armor 5". Several cards
/// can carry the same effect, so the behaviour lives with the effect rather
/// than with any card.
/// </summary>
public record CardEffect(string Name, int Amount)
{
    public bool Is(string name) => Name.Equals(name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The effects the game knows how to run, and the numbers behind them.</summary>
public static class Effects
{
    public const string Burning = "Burning";
    public const string Armor = "Armor";
    public const string Nimble = "Nimble";

    public static readonly string[] Known = { Burning, Armor, Nimble };

    /// <summary>Damage each stack of Burning deals at the victim's turn start.</summary>
    public const int BurnDamagePerStack = 5;

    /// <summary>
    /// How many of the victim's turns one stack of Burning lasts. Each stack
    /// runs its own clock, so stacking never extends the ones already alight.
    /// </summary>
    public const int BurnTurns = 2;

    public static bool IsKnown(string name) =>
        Array.Exists(Known, k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Armor helps its holder, so a card carrying it aims at friends.</summary>
    public static bool IsFriendly(string name) =>
        name.Equals(Armor, StringComparison.OrdinalIgnoreCase);
}
