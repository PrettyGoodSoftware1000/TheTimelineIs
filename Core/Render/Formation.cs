using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;

namespace TheTimelineIs.Core.Render;

/// <summary>
/// Battle-stage layout. Each side has three rows (columns on screen):
/// players on the left with Back leftmost and Front rightmost; enemies
/// mirrored on the right. A row holds up to 3 characters, staggered down
/// and across so nobody hides behind anyone. Player sprites can be dragged
/// between rows.
/// </summary>
public static class Formation
{
    public const int RowCount = 3;           // 0 = Back, 1 = Mid, 2 = Front
    private const int ColWidth = 580;
    private const int PlayerX0 = 60;         // Back column of the player side
    private const int EnemyBackX = 3200;     // Back column of the enemy side

    /// <summary>Stage top, dropped 4 feet (4 x 180px) from the original 380.</summary>
    private const int StageTop = 380 + 4 * Ruler.UnitPx;
    private const int CellHeight = 620;
    private const int StackOffsetY = 130;    // each extra body in a row sits lower
    private const int StackOffsetX = 105;    // ...and a little to the side

    /// <summary>Recoil when hit: one full side-to-side wobble over a quarter second.</summary>
    public const float ShakeDuration = 0.25f;
    private const float ShakeAmplitude = 45f;

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

    /// <summary>Ticks down every character's recoil timer.</summary>
    public static void UpdateShakes(List<CharacterInstance> present, float dt)
    {
        foreach (var inst in present)
            if (inst.ShakeTimer > 0)
                inst.ShakeTimer = Math.Max(0f, inst.ShakeTimer - dt);
    }

    /// <summary>Sideways recoil: out and back exactly once over ShakeDuration.</summary>
    public static Vector2 ShakeOffset(CharacterInstance inst)
    {
        if (inst.ShakeTimer <= 0) return Vector2.Zero;
        float progress = 1f - inst.ShakeTimer / ShakeDuration;
        return new Vector2((float)Math.Sin(progress * MathHelper.TwoPi) * ShakeAmplitude, 0f);
    }

    /// <summary>
    /// Resting on-screen rect for every living character, before walk and
    /// recoil offsets are applied.
    /// </summary>
    public static List<(CharacterInstance Inst, Rectangle Rect)> Layout(
        GameContext ctx, List<CharacterInstance> present)
    {
        var result = new List<(CharacterInstance, Rectangle)>();
        foreach (var group in present.Where(i => i.Alive)
                     .GroupBy(i => (i.IsPlayer, Row: Math.Clamp(i.Row, 0, RowCount - 1))))
        {
            int x = ColumnX(group.Key.IsPlayer, group.Key.Row);
            int j = 0;
            foreach (var inst in group)
            {
                // stagger each additional body in the row down and toward the middle
                int shove = group.Key.IsPlayer ? j * StackOffsetX : -j * StackOffsetX;
                var cell = new Rectangle(x + 20 + shove, StageTop + j * StackOffsetY,
                    ColWidth - 40, CellHeight);
                var tex = ctx.Assets.LoadTexture(inst.SpritePath);
                var rect = Ui.FitCentered(AssetLoader.DisplaySize(tex, AssetKind.Sprite), cell);
                result.Add((inst, ScaleAboutFeet(rect, ctx.Config.CastScale(inst.Name))));
                j++;
            }
        }
        return result;
    }

    /// <summary>Where a character currently is, including walk and recoil.</summary>
    public static Rectangle CurrentRect(GameContext ctx, List<CharacterInstance> present,
        CharacterInstance inst)
    {
        var found = Layout(ctx, present).FirstOrDefault(e => e.Inst == inst);
        if (found.Inst == null) return Rectangle.Empty;
        return Offset(found.Rect, inst.WalkOffset + ShakeOffset(inst));
    }

    private static Rectangle Offset(Rectangle r, Vector2 by) =>
        new(r.X + (int)by.X, r.Y + (int)by.Y, r.Width, r.Height);

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
    /// Shared stage rendering: sprites (with walk and recoil applied), a small
    /// HP bar under each one, an underline on whoever's turn it is, target
    /// highlights, and the dragged sprite ghosting under the pointer.
    /// </summary>
    public static void DrawCast(SpriteBatch batch, GameContext ctx,
        List<CharacterInstance> present, CharacterInstance? currentTurn,
        List<CharacterInstance>? targets, CharacterInstance? dragging, Point pointer)
    {
        if (dragging != null)
            for (int row = 0; row < RowCount; row++)
            {
                var zone = new Rectangle(ColumnX(true, row) + 10, StageTop - 20,
                    ColWidth - 20, CellHeight + 2 * StackOffsetY + 120);
                Ui.FillRect(batch, ctx.Pixel, zone, Color.White * 0.10f);
            }

        foreach (var (inst, baseRect) in Layout(ctx, present))
        {
            if (inst == dragging)
            {
                var ghost = new Rectangle(pointer.X - baseRect.Width / 2,
                    pointer.Y - baseRect.Height / 2, baseRect.Width, baseRect.Height);
                batch.Draw(ctx.Assets.LoadTexture(inst.SpritePath), ghost, Color.White * 0.6f);
                continue;
            }

            var rect = Offset(baseRect, inst.WalkOffset + ShakeOffset(inst));
            batch.Draw(ctx.Assets.LoadTexture(inst.SpritePath), rect, Color.White);

            if (inst == currentTurn)
                Ui.FillRect(batch, ctx.Pixel,
                    new Rectangle(rect.X, rect.Bottom + 54, rect.Width, 8), Color.Gold);
            if (targets != null && targets.Contains(inst))
                Ui.FillRect(batch, ctx.Pixel,
                    new Rectangle(rect.X, rect.Y - 26, rect.Width, 12), Color.OrangeRed);

            // compact HP bar, tucked under the feet
            int barW = Math.Min(190, rect.Width);
            var back = new Rectangle(rect.X + (rect.Width - barW) / 2, rect.Bottom + 14, barW, 14);
            Ui.FillRect(batch, ctx.Pixel, back, Color.Black * 0.65f);
            int fill = (int)(barW * Math.Clamp(inst.Hp / (float)inst.MaxHp, 0f, 1f));
            Ui.FillRect(batch, ctx.Pixel, new Rectangle(back.X, back.Y, fill, back.Height),
                inst.IsPlayer ? new Color(70, 190, 70) : new Color(200, 60, 60));
        }
    }
}
