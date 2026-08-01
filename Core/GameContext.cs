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
    public required CardLibrary Cards;
    public required ClassLibrary Classes;
    public required SoundBank Sounds;
    public IDevDestinationWriter? DevWriter;

    public IScreen Screen = null!;

    public void SwitchTo(IScreen screen) => Screen = screen;

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
