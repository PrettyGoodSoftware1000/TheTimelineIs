using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Screens;

/// <summary>
/// The player died. Reload drops them at their last save; with no save,
/// mission progress is lost and they return to the map.
/// </summary>
public class DeathScreen : IScreen
{
    private readonly GameContext _ctx;
    private Point? _tap;

    private static readonly Rectangle ReloadRect = new(1620, 1250, 600, 180);

    public DeathScreen(GameContext ctx) => _ctx = ctx;

    public void Update(InputState input, float dt) => _tap = input.Tap;

    public void Draw(SpriteBatch batch)
    {
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height), Color.Black);
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("death_title"),
            new Rectangle(0, 700, VirtualViewport.Width, 250), new Color(200, 30, 30), 1.2f);

        if (!_ctx.SaveStore.Exists)
            Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("death_no_save"),
                new Rectangle(0, 1020, VirtualViewport.Width, 120), Color.Gray, 0.5f);

        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, ReloadRect, _ctx.Strings.Get("death_reload"), _tap))
            _ctx.LoadLastSave();

        _tap = null;
    }
}
