using Microsoft.Xna.Framework;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Platform;

namespace TheTimelineIs.Desktop;

public class DesktopPlatform : IPlatform
{
    public ISaveStore SaveStore { get; } = new DesktopSaveStore();
    public IDevDestinationWriter? DevWriter { get; }

    public DesktopPlatform(bool devMap)
    {
        if (devMap)
            DevWriter = new DesktopDevDestinationWriter();
    }

    public IInputSource CreateInput(Game game) => new DesktopInput(game);
}
