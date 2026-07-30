using System.Linq;
using TheTimelineIs.Core;
using TheTimelineIs.Desktop;

bool devMap = args.Contains("--devmap");
using var game = new TimelineGame(new DesktopPlatform(devMap));
game.Run();
