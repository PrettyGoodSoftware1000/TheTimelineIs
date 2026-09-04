using System;
using System.Linq;
using TheTimelineIs.Core;
using TheTimelineIs.Desktop;

// --devmap  lets a click on the world map write a destination into Destinations.txt
// --level X opens level X straight away, skipping the title and the map
//
// The map editor and the sprite tool are gone from this branch, and the pixel
// renderer is no longer optional — it is how the game draws.
bool devMap = args.Contains("--devmap");
int at = Array.IndexOf(args, "--level");
string level = at >= 0 && at + 1 < args.Length ? args[at + 1] : "";

using var game = new TimelineGame(new DesktopPlatform(devMap, level));
game.Run();
