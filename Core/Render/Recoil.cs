using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using TheTimelineIs.Core.Data;

namespace TheTimelineIs.Core.Render;

/// <summary>
/// The flinch a character gives when something lands on it: one full
/// side-to-side wobble over a quarter of a second.
///
/// This is what is left of the old Formation helper, which also laid the cast
/// out in three rows down each side of a flat stage. The isometric levels place
/// everyone on a grid instead, so the layout half of it went with the side-view
/// screens; only the recoil was ever shared.
/// </summary>
public static class Recoil
{
    public const float Duration = 0.25f;
    private const float Amplitude = 45f;

    /// <summary>Ticks down every character's recoil timer.</summary>
    public static void Update(IEnumerable<CharacterInstance> present, float dt)
    {
        foreach (var inst in present)
            if (inst.ShakeTimer > 0)
                inst.ShakeTimer = Math.Max(0f, inst.ShakeTimer - dt);
    }

    /// <summary>Sideways displacement: out and back exactly once over Duration.</summary>
    public static Vector2 Offset(CharacterInstance inst)
    {
        if (inst.ShakeTimer <= 0) return Vector2.Zero;
        float progress = 1f - inst.ShakeTimer / Duration;
        return new Vector2((float)Math.Sin(progress * MathHelper.TwoPi) * Amplitude, 0f);
    }
}
