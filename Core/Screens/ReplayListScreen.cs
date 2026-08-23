using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Iso;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Core.Screens;

/// <summary>
/// The list of recorded missions, newest first. Picking one loads the level as
/// it was when that mission was played — the copy kept beside the record — and
/// steps through what happened, a turn per press.
/// </summary>
public class ReplayListScreen : IScreen
{
    private const int Rows = 9;

    private readonly GameContext _ctx;
    private readonly IReadOnlyList<string> _names;
    private Point? _tap;
    private int _page;

    private static readonly Rectangle BackRect = new(120, 1780, 460, 150);
    private static readonly Rectangle PrevRect = new(2560, 1780, 300, 150);
    private static readonly Rectangle NextRect = new(2900, 1780, 300, 150);

    public ReplayListScreen(GameContext ctx)
    {
        _ctx = ctx;
        _names = ctx.ReplayStore.List();
    }

    public void Update(InputState input, float dt)
    {
        _tap = input.Tap;
        if (input.Cancel) _ctx.SwitchTo(new TitleScreen(_ctx));
    }

    public void Draw(SpriteBatch batch)
    {
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height), new Color(12, 12, 24));
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("menu_replays"),
            new Rectangle(0, 180, VirtualViewport.Width, 160), Color.White, 1.1f);

        if (_names.Count == 0)
        {
            Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("replay_none"),
                new Rectangle(0, 900, VirtualViewport.Width, 100), Color.White * 0.7f, 0.44f);
        }
        else
        {
            int first = _page * Rows;
            for (int i = 0; i < Rows && first + i < _names.Count; i++)
            {
                string name = _names[first + i];
                var row = new Rectangle(700, 450 + i * 140, 2440, 120);
                if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, row, name, _tap))
                    Open(name);
            }

            int pages = (_names.Count + Rows - 1) / Rows;
            if (pages > 1)
            {
                if (_page > 0 && Ui.Button(batch, _ctx.Pixel, _ctx.Font, PrevRect, "<", _tap)) _page--;
                if (_page < pages - 1 && Ui.Button(batch, _ctx.Pixel, _ctx.Font, NextRect, ">", _tap)) _page++;
            }
        }

        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, BackRect, _ctx.Strings.Get("error_continue"), _tap))
            _ctx.SwitchTo(new TitleScreen(_ctx));
        _tap = null;
    }

    /// <summary>
    /// A replay whose halves cannot be read is reported rather than opened —
    /// a half-written pair on disk should say so, not crash on a null level.
    /// </summary>
    private void Open(string name)
    {
        if (_ctx.ReplayStore.Load(name) is not var (replayText, levelText) ||
            replayText == null || levelText == null)
        {
            _ctx.ReportProblem($"Replays/{name}.txt", "could not be read");
            return;
        }
        var replay = Replay.Parse(replayText);
        _ctx.State.Reset(replay.Party.Count > 0 ? replay.Party : _ctx.State.PartyOrDefault());
        _ctx.SwitchTo(new IsoLevelScreen(_ctx, name, replay, levelText));
    }
}
