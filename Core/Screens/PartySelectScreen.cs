using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Screens;

/// <summary>
/// New Game: pick 3 party members from the playable classes (those in
/// Classes.txt that have a PlayerCharacters folder). Duplicates are allowed —
/// three Dirtbags is a legal, if fragrant, party. Click a class to add it,
/// click a filled slot to clear it, Start when all 3 are chosen.
/// </summary>
public class PartySelectScreen : IScreen
{
    private readonly GameContext _ctx;
    private readonly List<string> _classes;
    private readonly List<string> _picked = new();
    private Point? _tap;

    private const int SlotW = 480, SlotH = 700, SlotY = 320;
    private const int OptW = 480, OptH = 640, OptY = 1180;
    private static readonly Rectangle StartRect = new(1620, 1930, 600, 170);

    public PartySelectScreen(GameContext ctx)
    {
        _ctx = ctx;
        _classes = ctx.Classes.ClassNames.Where(HasPlayerFolder).ToList();
        if (_classes.Count == 0) // Classes.txt missing or empty — don't strand the player
            _classes = new List<string> { "Dirtbag", "Gun-O-Mancer", "Cyborg" };
    }

    private static bool HasPlayerFolder(string name) =>
        AssetLoader.TryReadLines($"Content/Cast/PlayerCharacters/{name}/{name}.txt").Count > 0;

    private static string SpritePathFor(string name)
    {
        var manifest = CastManifest.Get(name);
        return $"Content/Cast/PlayerCharacters/{name}/{manifest.Variants[0]}";
    }

    public void Update(InputState input, float dt)
    {
        _tap = input.Tap;
        if (input.Cancel)
            _ctx.SwitchTo(new TitleScreen(_ctx));
    }

    public void Draw(SpriteBatch batch)
    {
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height), new Color(12, 12, 24));
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("party_title"),
            new Rectangle(0, 90, VirtualViewport.Width, 160), Color.White, 0.7f);

        // the three party slots
        int slotsX = (VirtualViewport.Width - (3 * SlotW + 2 * 80)) / 2;
        for (int i = 0; i < 3; i++)
        {
            var rect = new Rectangle(slotsX + i * (SlotW + 80), SlotY, SlotW, SlotH);
            Ui.FillRect(batch, _ctx.Pixel, rect, new Color(30, 30, 45));
            if (i < _picked.Count)
            {
                var tex = _ctx.Assets.LoadTexture(SpritePathFor(_picked[i]));
                var fit = Ui.FitCentered(AssetLoader.DisplaySize(tex, AssetKind.Sprite),
                    new Rectangle(rect.X + 20, rect.Y + 20, rect.Width - 40, rect.Height - 130));
                batch.Draw(tex, fit, Color.White);
                Ui.DrawTextCentered(batch, _ctx.Font, _picked[i],
                    new Rectangle(rect.X, rect.Bottom - 110, rect.Width, 100), Color.White, 0.4f);
                if (_tap.HasValue && rect.Contains(_tap.Value))
                {
                    _picked.RemoveAt(i);
                    _tap = null;
                }
            }
            else
            {
                Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("party_slot_empty"),
                    rect, Color.Gray, 0.4f);
            }
        }

        // the class options
        int optsX = (VirtualViewport.Width - (_classes.Count * OptW + (_classes.Count - 1) * 60)) / 2;
        for (int i = 0; i < _classes.Count; i++)
        {
            var rect = new Rectangle(optsX + i * (OptW + 60), OptY, OptW, OptH);
            Ui.FillRect(batch, _ctx.Pixel, rect, new Color(24, 34, 24));
            var tex = _ctx.Assets.LoadTexture(SpritePathFor(_classes[i]));
            var fit = Ui.FitCentered(AssetLoader.DisplaySize(tex, AssetKind.Sprite),
                new Rectangle(rect.X + 16, rect.Y + 16, rect.Width - 32, rect.Height - 120));
            batch.Draw(tex, fit, Color.White);
            Ui.DrawTextCentered(batch, _ctx.Font, _classes[i],
                new Rectangle(rect.X, rect.Bottom - 100, rect.Width, 90), Color.White, 0.38f);
            if (_tap.HasValue && rect.Contains(_tap.Value) && _picked.Count < 3)
            {
                _picked.Add(_classes[i]);
                _tap = null;
            }
        }

        if (_picked.Count == 3 &&
            Ui.Button(batch, _ctx.Pixel, _ctx.Font, StartRect, _ctx.Strings.Get("party_start"), _tap))
        {
            _ctx.State.Reset(_picked.ToList());
            _ctx.SwitchTo(new MapScreen(_ctx));
        }

        _tap = null;
    }
}
