using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Input;

namespace TheTimelineIs.Core.Screens;

public interface IScreen
{
    void Update(InputState input, float deltaSeconds);
    /// <summary>Called inside a SpriteBatch.Begin/End pair in virtual coordinates.</summary>
    void Draw(SpriteBatch batch);
}

/// <summary>
/// A screen that runs its own SpriteBatch instead of being handed one already
/// set up for the 3840x2160 design space.
///
/// That design space exists to letterbox a fixed layout onto any window, which
/// means scaling — and the pixel build's whole point is that nothing scales. A
/// screen implementing this gets the raw backbuffer and decides for itself.
/// </summary>
public interface IDrawsItself
{
    void DrawSelf(SpriteBatch batch, GraphicsDevice device);
}
