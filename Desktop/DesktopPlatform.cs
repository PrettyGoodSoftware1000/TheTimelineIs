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
    private readonly bool _editor, _pixel;

    public DesktopPlatform(bool devMap, bool editor, bool pixel = false)
    {
        _editor = editor;
        _pixel = pixel;
        if (devMap)
            DevWriter = new DesktopDevDestinationWriter();
    }

    public IInputSource CreateInput(Game game) => new DesktopInput(game);

    public IDevDestinationWriter? CreateDevWriter() => new DesktopDevDestinationWriter();

    public TheTimelineIs.Core.Screens.IScreen? CreateEditorScreen(TheTimelineIs.Core.GameContext ctx) =>
        _editor ? new IsoEditorScreen(ctx)
        : _pixel ? new TheTimelineIs.Core.Pixel.PixelScreen(ctx)
        : null;
}
