using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Iso;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Screens;

/// <summary>
/// The scrolling world map. Destinations come from Destinations.txt in
/// map-image pixel coordinates. With --devmap, tapping an empty spot starts
/// the click-to-place flow: type a name, then a mission folder name, and the
/// row is appended to the source Destinations.txt.
/// </summary>
public class MapScreen : IScreen
{
    private const int MarkerRadius = 60;      // draw size
    private const int TapRadius = 100;        // generous touch target

    private readonly GameContext _ctx;
    private readonly Texture2D _map;
    private readonly Point _mapSize;   // after undersized-art scale-up and the global config scale
    private readonly float _scale;     // Config.txt global scale: destination coords scale with the map
    private readonly DestinationTable _destinations;
    private Vector2 _camera;
    private Point? _tap;
    private float _toastTimer;
    private string _toastText = "";

    // dev-mode placement state
    private enum DevPhase { Idle, TypingName, TypingMission }
    private DevPhase _devPhase = DevPhase.Idle;
    private bool _devWasOn;
    private Point _devPoint;
    private string _devName = "";
    private string _devMission = "";

    private static readonly Rectangle SaveRect = new(60, 60, 400, 160);

    public MapScreen(GameContext ctx)
    {
        _ctx = ctx;
        _map = ctx.Assets.LoadTexture("Content/Images/Map/Map.png");
        _scale = ctx.Config.GlobalScale;
        var size = AssetLoader.DisplaySize(_map, AssetKind.Map) * _scale;
        _mapSize = new Point((int)size.X, (int)size.Y);
        _destinations = DestinationTable.Load();
        // starting with --devmap is not news; only a change made with Ctrl+D is
        _devWasOn = ctx.DevWriter != null;
    }

    public void Update(InputState input, float dt)
    {
        // Ctrl+D is handled centrally, so it works from any screen. Noticing it
        // here is what gives it something to say: the map is the only place the
        // mode does anything, so this is where it is worth reporting.
        bool devNow = _ctx.DevWriter != null;
        if (devNow != _devWasOn)
        {
            _devWasOn = devNow;
            Toast(_ctx.Strings.Get(devNow ? "devmap_on" : "devmap_off"));
            if (!devNow) _devPhase = DevPhase.Idle;   // don't leave a half-typed name up
        }

        bool typing = _devPhase != DevPhase.Idle;
        if (typing)
        {
            UpdateDevTyping(input);
        }
        else
        {
            _camera += input.PanDelta;
            _tap = input.Tap;
            if (input.Cancel)
                _ctx.SwitchTo(new TitleScreen(_ctx));
        }

        _camera.X = MathHelper.Clamp(_camera.X, 0, Math.Max(0, _mapSize.X - VirtualViewport.Width));
        _camera.Y = MathHelper.Clamp(_camera.Y, 0, Math.Max(0, _mapSize.Y - VirtualViewport.Height));
        if (_toastTimer > 0) _toastTimer -= dt;
    }

    private void UpdateDevTyping(InputState input)
    {
        bool typingName = _devPhase == DevPhase.TypingName;
        string buffer = typingName ? _devName : _devMission;
        buffer += input.TypedChars;
        if (input.Backspace && buffer.Length > 0)
            buffer = buffer[..^1];
        if (typingName) _devName = buffer; else _devMission = buffer;

        if (input.Cancel)
        {
            _devPhase = DevPhase.Idle;
            return;
        }
        if (!input.Submit) return;

        if (_devPhase == DevPhase.TypingName && _devName.Trim().Length > 0)
        {
            _devPhase = DevPhase.TypingMission;
        }
        else if (_devPhase == DevPhase.TypingMission && _devMission.Trim().Length > 0)
        {
            string name = _devName.Trim();
            string mission = ToPascalCase(_devMission);
            _ctx.DevWriter!.Append(name, _devPoint.X, _devPoint.Y, mission);
            _destinations.All.Add(new Destination(name, _devPoint.X, _devPoint.Y, mission));
            _devPhase = DevPhase.Idle;
            Toast(_ctx.Strings.Get("devmap_saved"));
        }
    }

    private void Toast(string text)
    {
        _toastText = text;
        _toastTimer = 2.5f;
    }

    /// <summary>"forest mission 2" -> "ForestMission2", matching the folder naming convention.</summary>
    private static string ToPascalCase(string input)
    {
        var words = input.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    public void Draw(SpriteBatch batch)
    {
        batch.Draw(_map, new Rectangle(
            -(int)_camera.X, -(int)_camera.Y, _mapSize.X, _mapSize.Y), Color.White);

        foreach (var dest in _destinations.All)
        {
            var screen = new Point((int)(dest.X * _scale - _camera.X), (int)(dest.Y * _scale - _camera.Y));
            bool done = _ctx.State.CompletedMissions.Contains(dest.Level);
            var rect = new Rectangle(screen.X - MarkerRadius, screen.Y - MarkerRadius,
                MarkerRadius * 2, MarkerRadius * 2);
            Ui.FillRect(batch, _ctx.Pixel, rect, done ? new Color(60, 160, 60) : new Color(190, 40, 40));
            Ui.DrawTextCentered(batch, _ctx.Font, dest.Name,
                new Rectangle(screen.X - 500, screen.Y + MarkerRadius, 1000, 90), Color.White, 0.4f);
        }

        if (_devPhase == DevPhase.Idle && _tap.HasValue)
            HandleTap(_tap.Value);

        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, SaveRect, _ctx.Strings.Get("map_save"), _tap))
        {
            _ctx.SaveStore.Save(_ctx.State.ToJson());
            Toast(_ctx.Strings.Get("saved"));
        }

        if (_ctx.DevWriter != null)
            DrawDevOverlay(batch);

        if (_toastTimer > 0)
            Ui.DrawTextCentered(batch, _ctx.Font, _toastText,
                new Rectangle(0, 1980, VirtualViewport.Width, 120), Color.LightGreen, 0.5f);

        _tap = null;
    }

    private void HandleTap(Point tap)
    {
        var world = new Point(tap.X + (int)_camera.X, tap.Y + (int)_camera.Y);

        foreach (var dest in _destinations.All)
        {
            int dx = world.X - (int)(dest.X * _scale), dy = world.Y - (int)(dest.Y * _scale);
            if (dx * dx + dy * dy <= TapRadius * TapRadius)
            {
                _ctx.State.StartMission(dest.Level);
                _ctx.SwitchTo(new IsoLevelScreen(_ctx, dest.Level));
                return;
            }
        }

        // empty spot: in dev mode, begin placement (but not on the save button).
        // stored point is in unscaled map-image pixels, like the file expects
        if (_ctx.DevWriter != null && !SaveRect.Contains(tap))
        {
            _devPoint = new Point((int)Math.Round(world.X / _scale), (int)Math.Round(world.Y / _scale));
            _devName = "";
            _devMission = "";
            _devPhase = DevPhase.TypingName;
        }
    }

    private void DrawDevOverlay(SpriteBatch batch)
    {
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("devmap_hint"),
            new Rectangle(0, 20, VirtualViewport.Width, 80), Color.Yellow, 0.4f);

        if (_devPhase == DevPhase.Idle) return;

        var screen = new Point((int)(_devPoint.X * _scale - _camera.X), (int)(_devPoint.Y * _scale - _camera.Y));
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(screen.X - 30, screen.Y - 30, 60, 60), Color.Yellow);

        var panel = new Rectangle(1120, 860, 1600, 440);
        Ui.FillRect(batch, _ctx.Pixel, panel, new Color(0, 0, 0, 230));
        string prompt = _devPhase == DevPhase.TypingName
            ? _ctx.Strings.Get("devmap_name_prompt")
            : _ctx.Strings.Get("devmap_mission_prompt");
        string buffer = _devPhase == DevPhase.TypingName ? _devName : _devMission;
        Ui.DrawTextCentered(batch, _ctx.Font, prompt,
            new Rectangle(panel.X, panel.Y + 40, panel.Width, 120), Color.Yellow, 0.5f);
        Ui.DrawTextCentered(batch, _ctx.Font, buffer + "_",
            new Rectangle(panel.X, panel.Y + 220, panel.Width, 120), Color.White, 0.5f);
    }
}
