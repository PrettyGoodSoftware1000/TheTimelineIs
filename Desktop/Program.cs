using System.Linq;
using TheTimelineIs.Core;
using TheTimelineIs.Desktop;

bool devMap = args.Contains("--devmap");
bool editor = args.Contains("--editor");
// the pixel build: same content, drawn so every pixel is the same size
bool pixel = args.Contains("--pixel");
using var game = new TimelineGame(new DesktopPlatform(devMap, editor, pixel));
game.Run();
