namespace TheTimelineIs.Core.Input;

/// <summary>
/// One key driving an overlay two ways at once.
///
///   TAP  — press and let go quickly: the overlay latches on and stays on
///          until the next tap turns it off. Good for reading a whole level.
///   HOLD — keep the key down past <see cref="HoldSeconds"/>: the overlay is
///          up while the key is, and goes away the moment it is released.
///          Good for a quick glance without having to remember to tap again.
///
/// The overlay comes up on the press either way, so it never feels laggy — a
/// hold is only told apart from a tap on the RELEASE, by how long the key was
/// down. Feed it the key's held state and the frame time every frame.
/// </summary>
public sealed class TapHold
{
    /// <summary>Held longer than this and the release turns the overlay off.</summary>
    public const float HoldSeconds = 1f;

    private bool _down;         // the key was down last frame
    private float _downFor;     // seconds it has been down for
    private bool _latched;      // left on by a tap

    /// <summary>Whether the overlay should be drawn this frame.</summary>
    public bool On => _latched || _down;

    /// <summary>True while the key is down and has not yet counted as a hold.</summary>
    public bool Deciding => _down && _downFor < HoldSeconds;

    public void Update(bool held, float dt)
    {
        if (held && !_down)
        {
            _down = true;
            _downFor = 0f;
            return;                     // shows immediately; what it MEANT waits for the release
        }
        if (held)
        {
            _downFor += dt;
            return;
        }
        if (!_down) return;             // still up, nothing happened

        // released: a quick press toggles the latch, a long one always clears it
        _latched = _downFor < HoldSeconds && !_latched;
        _down = false;
        _downFor = 0f;
    }

    /// <summary>Forces the overlay off — for switching levels, tools, or screens.</summary>
    public void Clear()
    {
        _latched = false;
        _down = false;
        _downFor = 0f;
    }
}
