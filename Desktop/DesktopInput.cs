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

    /// <summary>True on the frame a key goes down, false while it stays down.</summary>
    private bool Tapped(KeyboardState keys, Keys key) =>
        keys.IsKeyDown(key) && _prevKeys.IsKeyUp(key);

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

        // arrows and WASD are reported both together and separately, so a
        // screen that needs the letters for something else can drop them
        var arrows = Vector2.Zero;
        if (keys.IsKeyDown(Keys.Left)) arrows.X -= 1;
        if (keys.IsKeyDown(Keys.Right)) arrows.X += 1;
        if (keys.IsKeyDown(Keys.Up)) arrows.Y -= 1;
        if (keys.IsKeyDown(Keys.Down)) arrows.Y += 1;

        var letters = Vector2.Zero;
        if (keys.IsKeyDown(Keys.A)) letters.X -= 1;
        if (keys.IsKeyDown(Keys.D)) letters.X += 1;
        if (keys.IsKeyDown(Keys.W)) letters.Y -= 1;
        if (keys.IsKeyDown(Keys.S)) letters.Y += 1;

        // one step per press, for nudging a number a pixel at a time
        state.Nudge = new Point(
            (Tapped(keys, Keys.Right) ? 1 : 0) - (Tapped(keys, Keys.Left) ? 1 : 0),
            (Tapped(keys, Keys.Down) ? 1 : 0) - (Tapped(keys, Keys.Up) ? 1 : 0));

        state.PanDelta = KeyPan(arrows + letters, dt);
        state.PanDeltaNoLetters = KeyPan(arrows, dt);

        // right-drag pans opposite the cursor motion, like dragging a paper map.
        // Dragging belongs to both readings of the pan.
        if (mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Pressed)
        {
            var drag = new Vector2(mouse.X - _prevMouse.X, mouse.Y - _prevMouse.Y);
            var scrolled = viewport.ScreenToVirtual(drag);
            state.PanDelta -= scrolled;
            state.PanDeltaNoLetters -= scrolled;
        }

        if (mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
            state.Tap = viewport.ScreenToVirtual(new Point(mouse.X, mouse.Y));

        state.PointerPos = viewport.ScreenToVirtual(new Point(mouse.X, mouse.Y));
        state.RawPointer = new Point(mouse.X, mouse.Y);
        if (state.Tap.HasValue) state.RawTap = new Point(mouse.X, mouse.Y);
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
        {
            state.AltTap = viewport.ScreenToVirtual(new Point(mouse.X, mouse.Y));
            state.RawAltTap = new Point(mouse.X, mouse.Y);
        }

        state.Submit = Pressed(keys, Keys.Enter);

        // 1..9 then 0 for the tenth card, from the number row or the numpad
        for (int slot = 1; slot <= 10; slot++)
        {
            int digit = slot == 10 ? 0 : slot;
            if (Pressed(keys, Keys.D0 + digit) || Pressed(keys, Keys.NumPad0 + digit))
                state.CardKey = slot;
        }
        state.EndTurn = Pressed(keys, Keys.End) || Pressed(keys, Keys.Space);
        state.Confirm = state.Submit || Pressed(keys, Keys.Space);
        state.Cancel = Pressed(keys, Keys.Escape);
        state.ToggleRuler = Pressed(keys, Keys.F12);
        state.Delete = Pressed(keys, Keys.Delete) || Pressed(keys, Keys.Back);
        state.DeleteHeld = keys.IsKeyDown(Keys.Delete) || keys.IsKeyDown(Keys.Back);
        state.CtrlHeld = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
        state.ShiftHeld = keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift);
        state.AltHeld = keys.IsKeyDown(Keys.LeftAlt) || keys.IsKeyDown(Keys.RightAlt);
        state.SpaceHeld = keys.IsKeyDown(Keys.Space);
        state.Undo = state.CtrlHeld && Pressed(keys, Keys.Z);
        state.ToggleDevMap = state.CtrlHeld && Pressed(keys, Keys.D);
        state.ToggleControls = Pressed(keys, Keys.Insert);
        state.SelectAll = Pressed(keys, Keys.Tab);
        // OemTilde is the ` / ~ key; shifted or not, it means the same thing here
        state.ToggleDevMenu = Pressed(keys, Keys.OemTilde);
        state.Copy = state.CtrlHeld && Pressed(keys, Keys.C);
        state.Paste = state.CtrlHeld && Pressed(keys, Keys.V);
        if (mouse.MiddleButton == ButtonState.Pressed && _prevMouse.MiddleButton == ButtonState.Released)
            state.MiddleTap = viewport.ScreenToVirtual(new Point(mouse.X, mouse.Y));

        _prevKeys = keys;
        _prevMouse = mouse;
        return state;
    }

    private static Vector2 KeyPan(Vector2 dir, float dt)
    {
        if (dir == Vector2.Zero) return Vector2.Zero;
        dir.Normalize();
        return dir * KeyPanSpeed * dt;
    }

    private bool Pressed(KeyboardState keys, Keys key) =>
        keys.IsKeyDown(key) && !_prevKeys.IsKeyDown(key);
}
