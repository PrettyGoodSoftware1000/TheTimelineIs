using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;

namespace TheTimelineIs.Core.Render;

/// <summary>
/// Battle-stage layout. Each side has three rows (columns on screen):
/// players on the left with Back leftmost and Front rightmost; enemies
/// mirrored on the right. A row holds up to 3 characters, stacked with a
/// vertical offset. Player sprites can be dragged between rows.
/// </summary>
public static class Formation
{
    public const int RowCount = 3;           // 0 = Back, 1 = Mid, 2 = Front
    private const int ColWidth = 580;
    private const int PlayerX0 = 60;         // Back column of the player side
    private const int EnemyBackX = 3200;     // Back column of the enemy side
    private const int StageTop = 380;
    private const int CellHeight = 620;
    private const int StackOffset = 300;

    public static void AssignDefaultRows(List<CharacterInstance> present)
    {
        int p = 0, e = 0;
        foreach (var inst in present)
        {
            if (inst.Row >= 0) continue;
            inst.Row = 2 - ((inst.IsPlayer ? p++ : e++) % RowCount); // first arrival goes Front
        }
    }

    private static int ColumnX(bool isPlayer, int row) =>
        isPlayer ? PlayerX0 + row * ColWidth : EnemyBackX - row * ColWidth;

    /// <summary>
    /// On-screen rect for every living character, in draw order (top of a
    /// stack first, so lower characters overlap upward neighbors).
    /// </summary>
    public static List<(CharacterInstance Inst, Rectangle Rect)> Layout(
        GameContext ctx, List<CharacterInstance> present)
    {
        var result = new List<(CharacterInstance, Rectangle)>();
        foreach (var group in present.Where(i => i.Alive)
                     .GroupBy(i => (i.IsPlayer, Row: System.Math.Clamp(i.Row, 0, RowCount - 1))))
        {
            int x = ColumnX(group.Key.IsPlayer, group.Key.Row);
            int j = 0;
            foreach (var inst in group)
            {
                var cell = new Rectangle(x + 20, StageTop + j * StackOffset, ColWidth - 40, CellHeight);
                var tex = ctx.Assets.LoadTexture(inst.SpritePath);
                var rect = Ui.FitCentered(AssetLoader.DisplaySize(tex, AssetKind.Sprite), cell);
                result.Add((inst, ScaleAboutFeet(rect, ctx.Config.CastScale(inst.Name))));
                j++;
            }
        }
        return result;
    }

    /// <summary>Config scaling keeps the feet planted so rows stay readable.</summary>
    private static Rectangle ScaleAboutFeet(Rectangle r, float f)
    {
        if (f == 1f) return r;
        int w = (int)(r.Width * f), h = (int)(r.Height * f);
        return new Rectangle(r.X + (r.Width - w) / 2, r.Bottom - h, w, h);
    }

    /// <summary>Which player-side row a dropped point lands in, if any.</summary>
    public static int? PlayerRowAt(Point p)
    {
        if (p.X < PlayerX0 || p.X >= PlayerX0 + RowCount * ColWidth) return null;
        return (p.X - PlayerX0) / ColWidth;
    }

    /// <summary>
    /// Shared stage rendering for the room and battle screens: sprites, HP
    /// bars, an underline on whoever's turn it is, target highlights, and the
    /// dragged sprite ghosting along under the pointer with row guides shown.
    /// </summary>
    public static void DrawCast(SpriteBatch batch, GameContext ctx,
        List<CharacterInstance> present, CharacterInstance? currentTurn,
        List<CharacterInstance>? targets, CharacterInstance? dragging, Point pointer)
    {
        if (dragging != null)
            for (int row = 0; row < RowCount; row++)
            {
                var zone = new Rectangle(ColumnX(true, row) + 10, StageTop - 20,
                    ColWidth - 20, CellHeight + 2 * StackOffset + 40);
                Ui.FillRect(batch, ctx.Pixel, zone, Color.White * 0.10f);
            }

        foreach (var (inst, rect) in Layout(ctx, present))
        {
            if (inst == dragging)
            {
                var ghost = new Rectangle(pointer.X - rect.Width / 2, pointer.Y - rect.Height / 2,
                    rect.Width, rect.Height);
                batch.Draw(ctx.Assets.LoadTexture(inst.SpritePath), ghost, Color.White * 0.6f);
                continue;
            }

            var tex = ctx.Assets.LoadTexture(inst.SpritePath);
            batch.Draw(tex, rect, Color.White);

            if (inst == currentTurn)
                Ui.FillRect(batch, ctx.Pixel,
                    new Rectangle(rect.X, rect.Bottom + 6, rect.Width, 10), Color.Gold);
            if (targets != null && targets.Contains(inst))
                Ui.FillRect(batch, ctx.Pixel,
                    new Rectangle(rect.X, rect.Y - 26, rect.Width, 12), Color.OrangeRed);

            // HP bar above the head
            int barW = System.Math.Min(300, rect.Width);
            var back = new Rectangle(rect.X + (rect.Width - barW) / 2, rect.Y - 46, barW, 22);
            Ui.FillRect(batch, ctx.Pixel, back, Color.Black * 0.6f);
            int fill = (int)(barW * System.Math.Clamp(inst.Hp / (float)inst.MaxHp, 0f, 1f));
            Ui.FillRect(batch, ctx.Pixel, new Rectangle(back.X, back.Y, fill, back.Height),
                inst.IsPlayer ? new Color(70, 190, 70) : new Color(200, 60, 60));
        }
    }
}
