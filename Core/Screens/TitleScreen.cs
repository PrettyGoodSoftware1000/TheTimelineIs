using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Screens;

public class TitleScreen : IScreen
{
    private readonly GameContext _ctx;
    private Point? _tap;

    private static readonly Rectangle NewGameRect = new(1620, 1150, 600, 180);
    private static readonly Rectangle ContinueRect = new(1620, 1400, 600, 180);

    public TitleScreen(GameContext ctx) => _ctx = ctx;

    public void Update(InputState input, float dt)
    {
        _tap = input.Tap;
        if (input.Cancel)
            _ctx.Game.Exit();
    }

    public void Draw(SpriteBatch batch)
    {
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height),
            new Color(12, 12, 24));
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("title"),
            new Rectangle(0, 500, VirtualViewport.Width, 300), Color.White, 1.6f);

        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, NewGameRect, _ctx.Strings.Get("menu_new_game"), _tap))
            _ctx.SwitchTo(new PartySelectScreen(_ctx));

        if (_ctx.SaveStore.Exists &&
            Ui.Button(batch, _ctx.Pixel, _ctx.Font, ContinueRect, _ctx.Strings.Get("menu_continue"), _tap))
            _ctx.LoadLastSave();

        _tap = null;
    }
}
