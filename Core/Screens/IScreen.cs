using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Input;

namespace TheTimelineIs.Core.Screens;

public interface IScreen
{
    void Update(InputState input, float deltaSeconds);
    /// <summary>Called inside a SpriteBatch.Begin/End pair in virtual coordinates.</summary>
    void Draw(SpriteBatch batch);
}
