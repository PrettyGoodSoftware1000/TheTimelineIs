using System.Linq;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Screens;

/// <summary>
/// Plays one room of a mission: background, cast on stage (players left,
/// enemies right), and dialogue advancing on tap/confirm. [Battle!] hands
/// off to BattleScreen; winning resumes after the marker. When the last
/// entry of the last room finishes, the mission completes and we return
/// to the map.
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
        _present = CastResolver.EnterRoom(ctx.State.Instances, _room.Cast);
        _background = _room.Background.Length > 0
            ? ctx.Assets.LoadTexture($"Content/Images/Backgrounds/{_room.Background}")
            : ctx.Pixel;
        _entryIndex = 0;
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
            _ctx.SwitchTo(new BattleScreen(_ctx, this));
            return;
        }
        _tap = input.Tap;
        _advance = input.Confirm;
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
            // undersized backgrounds are scaled up, then fitted without distortion
            var size = AssetLoader.DisplaySize(_background, AssetKind.Background);
            batch.Draw(_background, Ui.FitCentered(size, screen), Color.White);
        }

        DrawCast(batch);

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

        // advance on confirm or any tap that wasn't the save button
        bool tapAdvance = _tap.HasValue && !tappedSave && !SaveRect.Contains(_tap.Value);
        if (_advance || tapAdvance)
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

    private void DrawCast(SpriteBatch batch)
    {
        var players = _present.Where(i => i.IsPlayer).ToList();
        var enemies = _present.Where(i => !i.IsPlayer).ToList();
        DrawSide(batch, players, left: true);
        DrawSide(batch, enemies, left: false);
    }

    private void DrawSide(SpriteBatch batch, List<CharacterInstance> side, bool left)
    {
        const int spriteHeight = 1050;  // virtual px on stage
        const int baseline = 1600;      // feet sit just above the dialogue box
        const int zoneWidth = 1650;
        int zoneStart = left ? 120 : VirtualViewport.Width - 120 - zoneWidth;

        int slot = zoneWidth / System.Math.Max(1, side.Count);
        for (int i = 0; i < side.Count; i++)
        {
            var tex = _ctx.Assets.LoadTexture(side[i].SpritePath);
            var size = AssetLoader.DisplaySize(tex, AssetKind.Sprite);
            // fit the stage slot without distortion; tall art keeps its full height
            var stage = new Rectangle(zoneStart + slot * i, baseline - spriteHeight, slot, spriteHeight);
            batch.Draw(tex, Ui.FitCentered(size, stage), Color.White);
        }
    }

    private void DrawDialogue(SpriteBatch batch, DialogueEntry dialogue)
    {
        Ui.FillRect(batch, _ctx.Pixel, DialogueBox, new Color(0, 0, 0, 210));

        var speaker = _present.FirstOrDefault(i =>
            i.Name.Equals(dialogue.Speaker, System.StringComparison.OrdinalIgnoreCase) && i.Alive);
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
