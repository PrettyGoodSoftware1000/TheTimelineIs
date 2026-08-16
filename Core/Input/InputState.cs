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

    /// <summary>
    /// The same pan with WASD left out — arrows and dragging only. The editor
    /// uses this so those letters can be tool hotkeys there, while the game
    /// keeps panning on them.
    /// </summary>
    public Vector2 PanDeltaNoLetters;

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

    /// <summary>Current pointer position (mouse cursor / last touch), virtual coords.</summary>
    public Point PointerPos;

    /// <summary>Primary button / finger currently held down (drives dragging).</summary>
    public bool PointerHeld;

    /// <summary>Where the pointer was released this frame, if it was.</summary>
    public Point? Released;

    /// <summary>F12: toggle the debug ruler overlay.</summary>
    public bool ToggleRuler;

    /// <summary>Mouse wheel notches this frame (positive = up). Editor uses it for height.</summary>
    public int ScrollDelta;

    /// <summary>
    /// Secondary click: the right button released without having been dragged,
    /// so panning the view never reads as a click.
    /// </summary>
    public Point? AltTap;

    /// <summary>Delete pressed this frame — the editor's erase.</summary>
    public bool Delete;

    /// <summary>Delete still down. Held over the grid, the editor rubs tiles out.</summary>
    public bool DeleteHeld;

    /// <summary>Either Ctrl down, for the editor's modified shortcuts.</summary>
    public bool CtrlHeld;

    /// <summary>
    /// Ctrl+Z this frame. It gets its own flag because TypedChars drops control
    /// characters, so the editor can never see it as a typed key.
    /// </summary>
    public bool Undo;

    /// <summary>Insert this frame: shows or hides the editor's controls panel.</summary>
    public bool ToggleControls;

    /// <summary>Ctrl+C / Ctrl+V this frame, for the same reason as Undo.</summary>
    public bool Copy, Paste;

    /// <summary>Either Shift down — the editor's box-place modifier.</summary>
    public bool ShiftHeld;

    /// <summary>Either Alt down — the editor writes every block's height while it is.</summary>
    public bool AltHeld;

    /// <summary>Middle click this frame: the editor's eyedropper.</summary>
    public Point? MiddleTap;
}
