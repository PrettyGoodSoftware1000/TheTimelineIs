using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Screens;

/// <summary>
/// Plays one room of a mission: background, cast arranged in formation rows,
/// dialogue advancing on tap/confirm. Player sprites can be dragged between
/// the three player-side rows. [Battle!] hands off to BattleScreen; when the
/// last entry of the last room finishes, the mission completes.
/// </summary>
public class RoomScreen : IScreen
{
    private readonly GameContext _ctx;
    private readonly MissionScript _script;
    private readonly Room _room;
    private readonly List<CharacterInstance> _present;
    private readonly Texture2D _background;
    private int _entryIndex;
    private Point? _tap;
    private bool _advance;
    private float _toastTimer;
    private CharacterInstance? _dragging;
    private Point _pointer;

    private static readonly Rectangle SaveRect = new(60, 60, 400, 160);
    private static readonly Rectangle DialogueBox = new(60, 1640, 3720, 460);

    public RoomScreen(GameContext ctx, MissionScript script)
    {
        _ctx = ctx;
        _script = script;

        // Snapshot BEFORE resolving so a room save replays the room cleanly.
        ctx.State.TakeRoomSnapshot();

        _room = script.Rooms.Count > ctx.State.RoomIndex
            ? script.Rooms[ctx.State.RoomIndex]
            : new Room();
        _present = CastResolver.EnterRoom(ctx.State.Instances, ExpandCast(_room.Cast, ctx.State));
        Formation.AssignDefaultRows(_present);
        _background = _room.Background.Length > 0
            ? ctx.Assets.LoadTexture($"Content/Images/Backgrounds/{_room.Background}")
            : ctx.Pixel;
        _entryIndex = 0;
    }

    /// <summary>"Player characters" in a Cast line becomes the chosen party.</summary>
    public static List<string> ExpandCast(List<string> cast, GameState state)
    {
        var result = new List<string>();
        foreach (var name in cast)
            if (name.Equals("Player characters", StringComparison.OrdinalIgnoreCase))
                result.AddRange(state.PartyOrDefault());
            else
                result.Add(name);
        return result;
    }

    /// <summary>Called by BattleScreen when the player wins: skip past the marker.</summary>
    public void ResumeAfterBattle()
    {
        _entryIndex++;
        if (_entryIndex >= _room.Entries.Count)
            NextRoomOrMap();
        else
            _ctx.SwitchTo(this);
    }

    public void Update(InputState input, float dt)
    {
        // a [Battle!] marker fires as soon as it's current — no tap needed
        if (_entryIndex < _room.Entries.Count && _room.Entries[_entryIndex] is BattleEntry)
        {
            _ctx.SwitchTo(new BattleScreen(_ctx, this, _present, _background));
            return;
        }

        _pointer = input.PointerPos;
        _tap = input.Tap;
        _advance = input.Confirm;

        // press on a player sprite starts a row drag instead of advancing
        if (_tap is Point press && _dragging == null)
        {
            var layout = Formation.Layout(_ctx, _present);
            for (int i = layout.Count - 1; i >= 0; i--)
                if (layout[i].Inst.IsPlayer && layout[i].Rect.Contains(press))
                {
                    _dragging = layout[i].Inst;
                    _tap = null;
                    break;
                }
        }
        if (_dragging != null && input.Released is Point drop)
        {
            if (Formation.PlayerRowAt(drop) is int row)
                _dragging.Row = row;
            _dragging = null;
        }

        if (_toastTimer > 0) _toastTimer -= dt;
    }

    public void Draw(SpriteBatch batch)
    {
        var screen = new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height);
        if (_background == _ctx.Pixel)
        {
            batch.Draw(_background, screen, new Color(20, 30, 20));
        }
        else
        {
            var size = AssetLoader.DisplaySize(_background, AssetKind.Background)
                * _ctx.Config.GlobalScale;
            batch.Draw(_background, Ui.FitCentered(size, screen), Color.White);
        }

        Formation.DrawCast(batch, _ctx, _present, null, null, _dragging, _pointer);

        var entry = _entryIndex < _room.Entries.Count ? _room.Entries[_entryIndex] : null;
        if (entry is DialogueEntry dialogue)
            DrawDialogue(batch, dialogue);

        bool tappedSave = Ui.Button(batch, _ctx.Pixel, _ctx.Font, SaveRect, _ctx.Strings.Get("room_save"), _tap);
        if (tappedSave)
        {
            _ctx.SaveStore.Save(_ctx.State.ToJson("room"));
            _toastTimer = 2.5f;
        }
        if (_toastTimer > 0)
            Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("saved"),
                new Rectangle(0, 240, 1000, 100), Color.LightGreen, 0.45f);

        bool tapAdvance = _tap.HasValue && !tappedSave && !SaveRect.Contains(_tap.Value);
        if ((_advance || tapAdvance) && _dragging == null)
            Advance();

        _tap = null;
        _advance = false;
    }

    private void Advance()
    {
        _entryIndex++;
        if (_entryIndex >= _room.Entries.Count)
            NextRoomOrMap();
        // a newly-current BattleEntry is picked up by the next Update
    }

    private void NextRoomOrMap()
    {
        if (_ctx.State.RoomIndex + 1 < _script.Rooms.Count)
        {
            _ctx.State.RoomIndex++;
            _ctx.SwitchTo(new RoomScreen(_ctx, _script));
        }
        else
        {
            _ctx.State.EndMission(completed: true);
            _ctx.SwitchTo(new MapScreen(_ctx));
        }
    }

    private void DrawDialogue(SpriteBatch batch, DialogueEntry dialogue)
    {
        Ui.FillRect(batch, _ctx.Pixel, DialogueBox, new Color(0, 0, 0, 210));

        var speaker = _present.FirstOrDefault(i =>
            i.Name.Equals(dialogue.Speaker, StringComparison.OrdinalIgnoreCase) && i.Alive);
        var thumbRect = new Rectangle(DialogueBox.X + 40, DialogueBox.Y + 38, 384, 384);
        if (speaker != null)
        {
            // a dedicated thumbnail if one exists, otherwise the full sprite
            var thumb = _ctx.Assets.LoadFirstAvailable(speaker.ThumbPath, speaker.SpritePath);
            var size = AssetLoader.DisplaySize(thumb, AssetKind.Thumb);
            batch.Draw(thumb, Ui.FitCentered(size, thumbRect), Color.White);
        }

        var namePos = new Vector2(DialogueBox.X + 480, DialogueBox.Y + 40);
        batch.DrawString(_ctx.Font, dialogue.Speaker, namePos, Color.Gold,
            0f, Vector2.Zero, 0.55f, SpriteEffects.None, 0f);

        string wrapped = Ui.Wrap(_ctx.Font, dialogue.Text, DialogueBox.Width - 560, 0.5f);
        batch.DrawString(_ctx.Font, wrapped, new Vector2(DialogueBox.X + 480, DialogueBox.Y + 150),
            Color.White, 0f, Vector2.Zero, 0.5f, SpriteEffects.None, 0f);
    }
}
