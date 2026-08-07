using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Audio;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Platform;
using TheTimelineIs.Core.Render;
using TheTimelineIs.Core.Screens;

namespace TheTimelineIs.Core;

/// <summary>Shared services handed to every screen.</summary>
public class GameContext
{
    public required Game Game;
    public required SpriteFont Font;
    public required Strings Strings;
    public required AssetLoader Assets;
    public required VirtualViewport Viewport;
    public required GameState State;
    public required ISaveStore SaveStore;
    public required Texture2D Pixel;
    public required GameConfig Config;
    /// <summary>Cards the party plays.</summary>
    public required CardLibrary Cards;

    /// <summary>Cards enemies act with — same format, read from EnemyCards.txt.</summary>
    public required CardLibrary EnemyCards;
    public required ClassLibrary Classes;
    public required EnemyLibrary Enemies;
    public required SoundBank Sounds;
    public required ILogStore LogStore;
    public IDevDestinationWriter? DevWriter;

    public IScreen Screen = null!;

    public void SwitchTo(IScreen screen) => Screen = screen;

    /// <summary>
    /// Raises a blocking popup over whatever is on screen and writes the log.
    /// Used for problems found mid-play, not just at startup.
    /// </summary>
    public void ReportProblem(string source, string message)
    {
        Diagnostics.Current.Error(source, 0, message);
        LogStore.Write(Diagnostics.Current.RenderLog());
        if (Screen is not ErrorScreen)
            SwitchTo(new ErrorScreen(this, Diagnostics.Current.Ordered(), LogStore.DisplayPath, Screen));
    }

    /// <summary>
    /// Death and the Continue button share this: load the last save and go
    /// where it says. A room save restarts its room from the first line.
    /// </summary>
    public void LoadLastSave()
    {
        string? json = SaveStore.Exists ? SaveStore.Load() : null;
        if (json == null)
        {
            State.EndMission(completed: false);
            SwitchTo(new MapScreen(this));
            return;
        }
        var data = State.ApplyJson(json);
        if (data.Location == "room" && State.CurrentMission != null)
            SwitchTo(new RoomScreen(this, MissionScript.Load(State.CurrentMission)));
        else
            SwitchTo(new MapScreen(this));
    }
}
