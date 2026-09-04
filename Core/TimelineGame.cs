using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Audio;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Platform;
using TheTimelineIs.Core.Render;
using TheTimelineIs.Core.Screens;

namespace TheTimelineIs.Core;

public class TimelineGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly IPlatform _platform;
    private IInputSource _input = null!;
    private SpriteBatch _batch = null!;
    private GameContext _ctx = null!;
    private readonly VirtualViewport _viewport = new();

    public TimelineGame(IPlatform platform)
    {
        _platform = platform;
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1920,
            PreferredBackBufferHeight = 1080,
        };
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
    }

    protected override void Initialize()
    {
        Window.Title = "The Timeline Is";
        _input = _platform.CreateInput(this);
        base.Initialize();
    }

    protected override void LoadContent()
    {
        _batch = new SpriteBatch(GraphicsDevice);

        var pixel = new Texture2D(GraphicsDevice, 1, 1);
        pixel.SetData(new[] { Color.White });

        // everything the loaders complain about lands here
        Diagnostics.Current = new Diagnostics();

        _ctx = new GameContext
        {
            Game = this,
            Font = Content.Load<SpriteFont>("Fonts/Courier"),
            Strings = Strings.Load(),
            Assets = new AssetLoader(GraphicsDevice),
            Viewport = _viewport,
            State = new GameState(),
            SaveStore = _platform.SaveStore,
            Pixel = pixel,
            Config = GameConfig.Load(),
            Cards = CardLibrary.Load(CardLibrary.PlayerPath),
            EnemyCards = CardLibrary.Load(CardLibrary.EnemyPath),
            Classes = ClassLibrary.Load(),
            Enemies = EnemyLibrary.Load(),
            Sounds = new SoundBank(),
            LogStore = _platform.LogStore,
            ReplayStore = _platform.ReplayStore,
            ContentIndex = _platform.ContentIndex,
            DevWriter = _platform.DevWriter,
        };

        ContentValidator.Run(_ctx.Cards, _ctx.EnemyCards, _ctx.Classes, _ctx.Enemies, _ctx.Strings,
            _ctx.Assets);
        _platform.LogStore.Write(Diagnostics.Current.RenderLog());

        IScreen home = _platform.CreateEditorScreen(_ctx) ?? new TitleScreen(_ctx);
        _ctx.SwitchTo(Diagnostics.Current.Any
            ? new ErrorScreen(_ctx, Diagnostics.Current.Ordered(),
                _platform.LogStore.DisplayPath, home)
            : home);
    }

    protected override void Update(GameTime gameTime)
    {
        _viewport.Update(GraphicsDevice);
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        var input = _input.Poll(_viewport, dt);
        if (input.ToggleRuler)
            _rulerVisible = !_rulerVisible;
        // ~ arms dev tools. On the map that means destination placement;
        // in a level the ~ menu handles the key itself.
        if (input.ToggleDevMap || (input.ToggleDevMenu && _ctx.Screen is MapScreen))
            ToggleDevMap();
        _ctx.Screen.Update(input, dt);
        base.Update(gameTime);
    }

    /// <summary>
    /// Ctrl+D turns map dev placement on and off in the running game, so
    /// marking a level no longer means quitting and relaunching with --devmap.
    /// The flag lives on the context, which the map screen reads every frame,
    /// so it takes effect immediately — and on the next visit to the map if it
    /// was pressed somewhere else. --devmap still works: it just starts here.
    /// </summary>
    private void ToggleDevMap()
    {
        _ctx.DevWriter = _ctx.DevWriter == null ? _platform.CreateDevWriter() : null;
        Console.WriteLine(_ctx.DevWriter == null
            ? "[devmap] off"
            : "[devmap] on — tap an empty spot on the map to place a destination");
    }

    private bool _rulerVisible;

    protected override void Draw(GameTime gameTime)
    {
        // reset to the full backbuffer before clearing — Apply() below narrows
        // the viewport to the letterboxed region and it persists across frames
        GraphicsDevice.Viewport = new Viewport(0, 0,
            GraphicsDevice.PresentationParameters.BackBufferWidth,
            GraphicsDevice.PresentationParameters.BackBufferHeight);
        GraphicsDevice.Clear(Color.Black);

        // A pixel screen gets the whole window and sets up its own batch. The
        // letterboxed design space below exists to scale a fixed layout onto
        // any window, which is the one thing a pixel build must not do.
        if (_ctx.Screen is IDrawsItself pixel)
        {
            pixel.DrawSelf(_batch, GraphicsDevice);
            base.Draw(gameTime);
            return;
        }

        _viewport.Apply(GraphicsDevice);
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
            SamplerState.LinearClamp, null, null, null, _viewport.Matrix);
        _ctx.Screen.Draw(_batch);
        if (_rulerVisible)
            Ruler.Draw(_batch, _ctx);
        _batch.End();
        base.Draw(gameTime);
    }
}
