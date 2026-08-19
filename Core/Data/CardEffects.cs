using System;
using System.Linq;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// One effect a card applies, e.g. "Burning 1", "Armor 5", "Form Witch".
/// Several cards can carry the same effect, so the behaviour lives with the
/// effect rather than with any card. Most take a number; a few (Form) take a
/// word instead, which lands in <see cref="Text"/>.
/// </summary>
public record CardEffect(string Name, int Amount, string Text = "")
{
    public bool Is(string name) => Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        Text.Length > 0 ? $"{Name} {Text}" : $"{Name} {Amount}";
}

/// <summary>The effects the game knows how to run, and the numbers behind them.</summary>
public static class Effects
{
    public const string Burning = "Burning";
    public const string Armor = "Armor";
    public const string Nimble = "Nimble";
    public const string Leap = "Leap";
    public const string Curse = "Curse";
    public const string Form = "Form";
    public const string Steal = "Steal";

    /// <summary>
    /// The card is cast over two turns. The first play starts the channel and
    /// locks the caster in place; the second aims and fires it.
    /// </summary>
    public const string Channel = "Channel";

    /// <summary>Leaves burning ground behind wherever the card landed.</summary>
    public const string FireTiles = "FireTiles";

    /// <summary>Longest first, so "Form" isn't mistaken for the start of something else.</summary>
    public static readonly string[] Known =
        { Burning, Armor, Nimble, Leap, Curse, Form, Steal, Channel, FireTiles };

    /// <summary>Damage each stack of Burning deals at the victim's turn start.</summary>
    public const int BurnDamagePerStack = 5;

    /// <summary>
    /// How many of the victim's turns one stack of Burning lasts. Each stack
    /// runs its own clock, so stacking never extends the ones already alight.
    /// </summary>
    public const int BurnTurns = 2;

    /// <summary>How many of the victim's turns a Curse lingers for.</summary>
    public const int CurseTurns = 10;

    /// <summary>
    /// Steal takes one card off whoever it hits, friend or foe, and hands it to
    /// the caster. "Steal N" means the thief holds it for N of their own turns
    /// counting the one they stole it on, so Steal 3 is "play it now, or on
    /// either of your next two turns". The card is unusable by its owner in the
    /// meantime and goes back the moment the thief plays it or the clock runs out.
    /// </summary>
    public const int StealTurns = 3;

    /// <summary>
    /// Burning ground doesn't deal damage of its own — it sets people alight.
    /// Starting a turn on it, walking through it, or ending a turn on it each
    /// add this many stacks of Burning, which then burn on the victim's clock.
    /// </summary>
    public const int FireTileStacks = 1;

    /// <summary>How many turns a burning square lasts before it goes out.</summary>
    public const int FireTileTurns = 2;

    public static bool IsKnown(string name) =>
        Known.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Armor helps its holder, so a card carrying it aims at friends.</summary>
    public static bool IsFriendly(string name) =>
        name.Equals(Armor, StringComparison.OrdinalIgnoreCase);

    /// <summary>Effects that act on the caster rather than on whatever was hit.</summary>
    public static bool IsSelfCast(string name) =>
        name.Equals(Channel, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(Nimble, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(Leap, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(Form, StringComparison.OrdinalIgnoreCase);

    /// <summary>These carry a word (a form name) instead of a number.</summary>
    public static bool TakesText(string name) =>
        name.Equals(Form, StringComparison.OrdinalIgnoreCase);
}
