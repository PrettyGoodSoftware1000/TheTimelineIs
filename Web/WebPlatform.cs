using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using TheTimelineIs.Core;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Platform;
using TheTimelineIs.Core.Screens;
using TheTimelineIs.Desktop;

namespace TheTimelineIs.Web;

/// <summary>
/// The browser build. It can read the game but write nothing: no save, no
/// replays, no dev tools, no editor. Keyboard and mouse work as on desktop.
/// </summary>
public class WebPlatform : IPlatform
{
    public ISaveStore SaveStore { get; } = new NoSave();
    public ILogStore LogStore { get; } = new ConsoleLog();
    public IReplayStore ReplayStore { get; } = new NoReplays();
    public IContentIndex ContentIndex { get; } = new WebContentIndex();
    public IDevDestinationWriter? DevWriter => null;

    public IInputSource CreateInput(Game game) => new DesktopInput(game);
    public IDevDestinationWriter? CreateDevWriter() => null;
    public IScreen? CreateStartScreen(GameContext ctx) => null;

    private class NoSave : ISaveStore
    {
        public bool CanSave => false;
        public bool Exists => false;
        public string? Load() => null;
        public void Save(string payload) { }
    }

    /// <summary>Content problems go to the browser console, which is F12 away.</summary>
    private class ConsoleLog : ILogStore
    {
        public string DisplayPath => "the browser console";
        public void Write(string contents) { if (contents.Length > 0) Console.WriteLine(contents); }
    }

    private class NoReplays : IReplayStore
    {
        public bool CanKeep => false;
        public string DisplayPath => "nowhere (the web version keeps no replays)";
        public IReadOnlyList<string> List() => Array.Empty<string>();
        public string? Save(string name, string replayText, string levelText) => null;
        public (string Replay, string Level)? Load(string name) => null;
    }
}
