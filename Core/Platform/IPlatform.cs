using Microsoft.Xna.Framework;
using TheTimelineIs.Core.Input;

namespace TheTimelineIs.Core.Platform;

/// <summary>
/// Everything a platform head (Desktop today, Android/iOS later) must provide.
/// Core never talks to Keyboard, Mouse, touch, or the file system directly —
/// it only sees these interfaces.
/// </summary>
public interface IPlatform
{
    IInputSource CreateInput(Game game);
    ISaveStore SaveStore { get; }

    /// <summary>Non-null only when running with --devmap on desktop.</summary>
    IDevDestinationWriter? DevWriter { get; }
}
