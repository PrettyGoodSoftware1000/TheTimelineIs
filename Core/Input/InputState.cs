using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Input;

/// <summary>
/// One frame of player intent, in virtual (3840x2160) coordinates.
/// Screens consume this and never know whether it came from a mouse,
/// keyboard, or a finger.
/// </summary>
public struct InputState
{
    /// <summary>How far the view should pan this frame, in virtual pixels.</summary>
    public Vector2 PanDelta;

    /// <summary>Where the player tapped/clicked this frame, if anywhere.</summary>
    public Point? Tap;

    /// <summary>Advance / accept (Enter, Space, or a tap on a button).</summary>
    public bool Confirm;

    /// <summary>Enter only — used by dev-mode text entry, where Space must type a space.</summary>
    public bool Submit;

    /// <summary>Back out / quit (Escape, or later the Android back button).</summary>
    public bool Cancel;

    /// <summary>Characters typed this frame. Used only by dev-mode text entry.</summary>
    public string TypedChars;

    /// <summary>Backspace pressed this frame (dev-mode text entry).</summary>
    public bool Backspace;
}
