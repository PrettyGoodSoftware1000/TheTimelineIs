using Microsoft.Xna.Framework;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Screens;

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
    ILogStore LogStore { get; }

    /// <summary>Where mission replays are written and read back.</summary>
    IReplayStore ReplayStore { get; }

    /// <summary>
    /// What is in a content folder. TitleContainer opens files by name but
    /// cannot say what exists, and some art is meant to be added by dropping
    /// pictures in rather than by listing them in a manifest.
    /// </summary>
    IContentIndex ContentIndex { get; }

    /// <summary>Non-null when the game STARTED with --devmap on desktop.</summary>
    IDevDestinationWriter? DevWriter { get; }

    /// <summary>
    /// A writer made on demand, for turning dev placement on with Ctrl+D while
    /// the game is already running. Null on a platform that cannot write back
    /// to the source tree, which is every platform but desktop.
    /// </summary>
    IDevDestinationWriter? CreateDevWriter();

    /// <summary>
    /// A screen to open instead of the title, when the platform was told to.
    /// Desktop uses it for --level, which drops straight into a named level
    /// rather than clicking through the title and the map to reach it. Null
    /// means the ordinary way in.
    /// </summary>
    IScreen? CreateStartScreen(GameContext ctx);
}
