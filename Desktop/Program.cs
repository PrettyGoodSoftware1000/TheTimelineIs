using System.Linq;
using TheTimelineIs.Core;
using TheTimelineIs.Desktop;

bool devMap = args.Contains("--devmap");
bool editor = args.Contains("--editor");
using var game = new TimelineGame(new DesktopPlatform(devMap, editor));
game.Run();
