using Microsoft.Xna.Framework;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Platform;

namespace TheTimelineIs.Desktop;

public class DesktopPlatform : IPlatform
{
    public ISaveStore SaveStore { get; } = new DesktopSaveStore();
    public ILogStore LogStore { get; } = new DesktopLogStore();
    public IReplayStore ReplayStore { get; } = new DesktopReplayStore();
    public IContentIndex ContentIndex { get; } = new DesktopContentIndex();
    public IDevDestinationWriter? DevWriter { get; }

    /// <summary>A level to open at once, from --level. Empty for the title screen.</summary>
    private readonly string _level;

    public DesktopPlatform(bool devMap = false, string level = "")
    {
        _level = level;
        if (devMap)
            DevWriter = new DesktopDevDestinationWriter();
    }

    public IInputSource CreateInput(Game game) => new DesktopInput(game);

    public IDevDestinationWriter? CreateDevWriter() => new DesktopDevDestinationWriter();

    /// <summary>
    /// --level opens a named level straight away, skipping the title and the
    /// map. It is how you look at a board you are working on without playing
    /// your way back to it every time.
    /// </summary>
    public TheTimelineIs.Core.Screens.IScreen? CreateStartScreen(TheTimelineIs.Core.GameContext ctx) =>
        _level.Length > 0 ? new TheTimelineIs.Core.Iso.IsoLevelScreen(ctx, _level) : null;
}
