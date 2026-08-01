using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;

namespace TheTimelineIs.Core.Audio;

/// <summary>
/// Plays raw WAV files from Content/Sounds/ through TitleContainer, the same
/// door the rest of the content uses. A missing or unreadable file is logged
/// once and then silently ignored, so cards can name sounds that don't exist
/// yet without breaking the battle or spamming the console.
/// </summary>
public class SoundBank
{
    private readonly Dictionary<string, SoundEffect?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public const string Folder = "Content/Sounds";

    public void Play(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;
        var effect = Get(fileName);
        try
        {
            effect?.Play();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[sound] could not play {fileName}: {ex.Message}");
        }
    }

    private SoundEffect? Get(string fileName)
    {
        if (_cache.TryGetValue(fileName, out var cached)) return cached;

        SoundEffect? effect = null;
        try
        {
            using var source = TitleContainer.OpenStream($"{Folder}/{fileName}");
            // mobile bundles hand back non-seekable streams; SoundEffect needs to seek
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            buffer.Position = 0;
            effect = SoundEffect.FromStream(buffer);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[sound] missing or unreadable: {Folder}/{fileName} ({ex.Message})");
        }
        _cache[fileName] = effect;
        return effect;
    }
}
