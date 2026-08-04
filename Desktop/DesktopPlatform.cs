using Microsoft.Xna.Framework;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Platform;

namespace TheTimelineIs.Desktop;

public class DesktopPlatform : IPlatform
{
    public ISaveStore SaveStore { get; } = new DesktopSaveStore();
    public ILogStore LogStore { get; } = new DesktopLogStore();
    public IDevDestinationWriter? DevWriter { get; }
    private readonly bool _editor;

    public DesktopPlatform(bool devMap, bool editor)
    {
        _editor = editor;
        if (devMap)
            DevWriter = new DesktopDevDestinationWriter();
    }

    public IInputSource CreateInput(Game game) => new DesktopInput(game);

    public TheTimelineIs.Core.Screens.IScreen? CreateEditorScreen(TheTimelineIs.Core.GameContext ctx) =>
        _editor ? new IsoEditorScreen(ctx) : null;
}
