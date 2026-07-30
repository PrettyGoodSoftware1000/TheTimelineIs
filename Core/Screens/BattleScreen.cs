using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Screens;

/// <summary>
/// Placeholder combat. Two outcomes only, which is the contract real combat
/// will honor later: win resumes the room after the [Battle!] marker, lose
/// means the player died and the last save reloads.
/// </summary>
public class BattleScreen : IScreen
{
    private readonly GameContext _ctx;
    private readonly RoomScreen _room;
    private Point? _tap;

    private static readonly Rectangle WinRect = new(1620, 1050, 600, 180);
    private static readonly Rectangle LoseRect = new(1520, 1330, 800, 180);

    public BattleScreen(GameContext ctx, RoomScreen room)
    {
        _ctx = ctx;
        _room = room;
    }

    public void Update(InputState input, float dt) => _tap = input.Tap;

    public void Draw(SpriteBatch batch)
    {
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height), new Color(40, 10, 10));
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("battle_placeholder"),
            new Rectangle(0, 600, VirtualViewport.Width, 200), Color.White, 0.8f);

        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, WinRect, _ctx.Strings.Get("battle_win"), _tap))
            _room.ResumeAfterBattle();

        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, LoseRect, _ctx.Strings.Get("battle_lose"), _tap,
                new Color(80, 20, 20, 220)))
            _ctx.SwitchTo(new DeathScreen(_ctx));

        _tap = null;
    }
}
