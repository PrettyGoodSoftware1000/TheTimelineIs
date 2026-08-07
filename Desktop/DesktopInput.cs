using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Desktop;

/// <summary>
/// Translates desktop input to the neutral InputState:
///   arrows / WASD      -> pan
///   right-button drag  -> pan (same gesture a finger drag will map to)
///   left click         -> tap
///   Enter / Space      -> confirm
///   Escape             -> cancel
/// A future TouchInput implements the same interface and nothing else changes.
/// </summary>
public class DesktopInput : IInputSource
{
    private const float KeyPanSpeed = 2200f; // virtual px/sec

    private KeyboardState _prevKeys;
    private MouseState _prevMouse;
    private Point _rightDownAt;
    private bool _rightDragged;
    private readonly StringBuilder _typed = new();
    private bool _backspace;

    public DesktopInput(Game game)
    {
        game.Window.TextInput += (_, e) =>
        {
            if (e.Character == '\b')
                _backspace = true;
            else if (!char.IsControl(e.Character))
                _typed.Append(e.Character);
        };
    }

    public InputState Poll(VirtualViewport viewport, float dt)
    {
        var keys = Keyboard.GetState();
        var mouse = Mouse.GetState();
        var state = new InputState { TypedChars = _typed.ToString(), Backspace = _backspace };
        _typed.Clear();
        _backspace = false;

        var pan = Vector2.Zero;
        if (keys.IsKeyDown(Keys.Left) || keys.IsKeyDown(Keys.A)) pan.X -= 1;
        if (keys.IsKeyDown(Keys.Right) || keys.IsKeyDown(Keys.D)) pan.X += 1;
        if (keys.IsKeyDown(Keys.Up) || keys.IsKeyDown(Keys.W)) pan.Y -= 1;
        if (keys.IsKeyDown(Keys.Down) || keys.IsKeyDown(Keys.S)) pan.Y += 1;
        if (pan != Vector2.Zero)
        {
            pan.Normalize();
            state.PanDelta += pan * KeyPanSpeed * dt;
        }

        // right-drag pans opposite the cursor motion, like dragging a paper map
        if (mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Pressed)
        {
            var drag = new Vector2(mouse.X - _prevMouse.X, mouse.Y - _prevMouse.Y);
            state.PanDelta -= viewport.ScreenToVirtual(drag);
        }

        if (mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
            state.Tap = viewport.ScreenToVirtual(new Point(mouse.X, mouse.Y));

        state.PointerPos = viewport.ScreenToVirtual(new Point(mouse.X, mouse.Y));
        state.PointerHeld = mouse.LeftButton == ButtonState.Pressed;
        if (mouse.LeftButton == ButtonState.Released && _prevMouse.LeftButton == ButtonState.Pressed)
            state.Released = state.PointerPos;

        state.ScrollDelta = (mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue) / 120;

        // right-drag pans, so a right-click only registers if the pointer stayed put
        if (mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Released)
        {
            _rightDownAt = new Point(mouse.X, mouse.Y);
            _rightDragged = false;
        }
        if (mouse.RightButton == ButtonState.Pressed &&
            Vector2.DistanceSquared(new Vector2(mouse.X, mouse.Y),
                new Vector2(_rightDownAt.X, _rightDownAt.Y)) > 36f)
            _rightDragged = true;
        if (mouse.RightButton == ButtonState.Released && _prevMouse.RightButton == ButtonState.Pressed
            && !_rightDragged)
            state.AltTap = viewport.ScreenToVirtual(new Point(mouse.X, mouse.Y));

        state.Submit = Pressed(keys, Keys.Enter);
        state.Confirm = state.Submit || Pressed(keys, Keys.Space);
        state.Cancel = Pressed(keys, Keys.Escape);
        state.ToggleRuler = Pressed(keys, Keys.F12);
        state.Delete = Pressed(keys, Keys.Delete) || Pressed(keys, Keys.Back);
        state.DeleteHeld = keys.IsKeyDown(Keys.Delete) || keys.IsKeyDown(Keys.Back);
        state.CtrlHeld = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
        state.Undo = state.CtrlHeld && Pressed(keys, Keys.Z);

        _prevKeys = keys;
        _prevMouse = mouse;
        return state;
    }

    private bool Pressed(KeyboardState keys, Keys key) =>
        keys.IsKeyDown(key) && !_prevKeys.IsKeyDown(key);
}
