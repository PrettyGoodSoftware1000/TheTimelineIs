using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Screens;

/// <summary>
/// New Game: pick a party of four from the classes declared in Classes.txt
/// that have a PlayerCharacters folder. Duplicates are allowed —
/// four Dirtbags is a legal, if fragrant, party. Click a class to add it,
/// click a filled slot to clear it, Start when all four are chosen.
/// </summary>
public class PartySelectScreen : IScreen
{
    private readonly GameContext _ctx;
    private readonly List<string> _classes;
    private readonly List<string> _picked = new();
    private Point? _tap;

    /// <summary>How many characters go into the field.</summary>
    public const int PartySize = 4;

    private const int SlotW = 480, SlotH = 700, SlotY = 320;
    private const int OptW = 480, OptH = 640, OptY = 1180;
    private static readonly Rectangle StartRect = new(1620, 1930, 600, 170);

    public PartySelectScreen(GameContext ctx)
    {
        _ctx = ctx;
        _classes = ctx.Classes.PlayableClasses();
    }

    private string SpritePathFor(string name)
    {
        var cls = _ctx.Classes.Get(name);
        string file = cls?.SpriteFiles[0] ?? $"{name}.png";
        return $"Content/Cast/PlayerCharacters/{name}/{file}";
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

        // the party slots
        int slotsX = (VirtualViewport.Width - (PartySize * SlotW + (PartySize - 1) * 80)) / 2;
        for (int i = 0; i < PartySize; i++)
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
            if (_tap.HasValue && rect.Contains(_tap.Value) && _picked.Count < PartySize)
            {
                _picked.Add(_classes[i]);
                _tap = null;
            }
        }

        if (_picked.Count == PartySize &&
            Ui.Button(batch, _ctx.Pixel, _ctx.Font, StartRect, _ctx.Strings.Get("party_start"), _tap))
        {
            _ctx.State.Reset(_picked.ToList());
            _ctx.SwitchTo(new MapScreen(_ctx));
        }

        _tap = null;
    }
}
