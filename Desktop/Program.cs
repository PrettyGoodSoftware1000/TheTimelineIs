using System;
using System.Linq;
using TheTimelineIs.Core;
using TheTimelineIs.Desktop;

// --devmap  lets a click on the world map write a destination into Destinations.txt
// --level X opens level X straight away, skipping the title and the map
// --editor  opens the level editor
bool devMap = args.Contains("--devmap");
bool editor = args.Contains("--editor");
int at = Array.IndexOf(args, "--level");
string level = at >= 0 && at + 1 < args.Length ? args[at + 1] : "";

using var game = new TimelineGame(new DesktopPlatform(devMap, level, editor));
game.Run();
