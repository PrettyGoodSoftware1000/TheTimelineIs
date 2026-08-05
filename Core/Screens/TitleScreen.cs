using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Screens;

public class TitleScreen : IScreen
{
    private const float ScrambleInterval = 0.125f;

    private readonly GameContext _ctx;
    private readonly string _word;
    private string _scrambled;
    private float _scrambleTimer;
    private Point? _tap;
    private static readonly Random Rng = new();

    private static readonly Rectangle NewGameRect = new(1620, 1250, 600, 180);
    private static readonly Rectangle ContinueRect = new(1620, 1490, 600, 180);

    public TitleScreen(GameContext ctx)
    {
        _ctx = ctx;
        _word = ctx.Strings.Get("title_scramble_word");
        _scrambled = Scramble(_word);
    }

    /// <summary>Shuffles until the result differs from the source, so it never reads straight.</summary>
    private static string Scramble(string word)
    {
        if (word.Length < 2) return word;
        string result;
        int guard = 0;
        do
        {
            result = new string(word.OrderBy(_ => Rng.Next()).ToArray());
        } while (string.Equals(result, word, StringComparison.OrdinalIgnoreCase) && ++guard < 50);
        return result;
    }

    public void Update(InputState input, float dt)
    {
        _tap = input.Tap;
        _scrambleTimer += dt;
        if (_scrambleTimer >= ScrambleInterval)
        {
            _scrambleTimer -= ScrambleInterval;
            _scrambled = Scramble(_word);
        }
        if (input.Cancel)
            _ctx.Game.Exit();
    }

    public void Draw(SpriteBatch batch)
    {
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height),
            new Color(12, 12, 24));

        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("title"),
            new Rectangle(0, 420, VirtualViewport.Width, 300), Color.White, 1.6f);
        Ui.DrawTextCentered(batch, _ctx.Font, _scrambled,
            new Rectangle(0, 740, VirtualViewport.Width, 300), new Color(220, 60, 60), 1.9f);

        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, NewGameRect, _ctx.Strings.Get("menu_new_game"), _tap))
            _ctx.SwitchTo(new PartySelectScreen(_ctx));

        if (_ctx.SaveStore.Exists &&
            Ui.Button(batch, _ctx.Pixel, _ctx.Font, ContinueRect, _ctx.Strings.Get("menu_continue"), _tap))
            _ctx.LoadLastSave();

        _tap = null;
    }
}
