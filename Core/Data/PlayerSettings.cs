using System;

namespace TheTimelineIs.Core.Data;

/// <summary>When a character's health and mind bars are on screen.</summary>
public enum BarVisibility
{
    /// <summary>Only the selected character's, which is how it has always been.</summary>
    Selected,

    /// <summary>Everybody's, all the time.</summary>
    Always,

    /// <summary>The selected one's, plus anybody who has lost health or nerve.</summary>
    Damaged,
}

/// <summary>
/// What the player has chosen in the Settings page, as opposed to what the
/// game is. These last the session: they are a preference about looking at
/// the game, not part of a save, and nothing here changes what happens.
/// </summary>
public class PlayerSettings
{
    public BarVisibility Bars = BarVisibility.Selected;

    /// <summary>
    /// Whether a card leaves the movement it has not spent.
    ///
    /// OFF is the game's own rule: most cards plant you, and choosing between
    /// walking and acting is the whole of a turn. On, a turn is a card and a
    /// walk in either order. A handful of cards root the caster whatever this
    /// says — see <see cref="Card.AlwaysStopsMovement"/>.
    /// </summary>
    public bool MoveAfterCard;

    /// <summary>The order the left and right arrows step through.</summary>
    public static readonly BarVisibility[] BarOrder =
        { BarVisibility.Always, BarVisibility.Selected, BarVisibility.Damaged };

    /// <summary>Steps the setting one place, wrapping at both ends.</summary>
    public void StepBars(int by)
    {
        int at = Array.IndexOf(BarOrder, Bars);
        Bars = BarOrder[((at + by) % BarOrder.Length + BarOrder.Length) % BarOrder.Length];
    }

    /// <summary>The Strings key for what the setting currently reads.</summary>
    public string BarsKey => Bars switch
    {
        BarVisibility.Always => "set_bars_always",
        BarVisibility.Damaged => "set_bars_damaged",
        _ => "set_bars_selected",
    };
}
