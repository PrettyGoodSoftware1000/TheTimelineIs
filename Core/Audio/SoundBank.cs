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
    private Dictionary<string, string>? _onDisk;

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

    /// <summary>
    /// How long a sound runs, in seconds — this is what "Casting Time: Use
    /// Sound Time" resolves to. Missing or silent sounds are zero.
    /// </summary>
    public float Duration(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return 0f;
        return (float?)Get(fileName)?.Duration.TotalSeconds ?? 0f;
    }

    private SoundEffect? Get(string fileName)
    {
        if (_cache.TryGetValue(fileName, out var cached)) return cached;

        SoundEffect? effect = null;
        try
        {
            using var source = TitleContainer.OpenStream($"{Folder}/{Resolve(fileName)}");
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

    /// <summary>
    /// Cards.txt is case-insensitive, but Linux file systems are not — map the
    /// authored name onto the real one. Directory listing doesn't exist inside
    /// a mobile bundle, so this quietly does nothing there and the exact name
    /// is used (which is correct on Windows and iOS anyway).
    /// </summary>
    private string Resolve(string fileName)
    {
        if (_onDisk == null)
        {
            _onDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "Content", "Sounds");
                if (Directory.Exists(dir))
                    foreach (var path in Directory.EnumerateFiles(dir))
                        _onDisk[Path.GetFileName(path)] = Path.GetFileName(path);
            }
            catch
            {
                // no directory listing available; exact-name lookup it is
            }
        }
        return _onDisk.TryGetValue(fileName, out var actual) ? actual : fileName;
    }
}
