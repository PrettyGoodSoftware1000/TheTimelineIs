using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Input;

public interface IInputSource
{
    /// <summary>
    /// Read the device once per frame and translate to virtual coordinates
    /// using the viewport's current scale/offset.
    /// </summary>
    InputState Poll(VirtualViewport viewport, float deltaSeconds);
}
