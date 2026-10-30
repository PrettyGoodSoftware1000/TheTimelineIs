using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Platform;

namespace TheTimelineIs.Web;

/// <summary>
/// Downloads every content file before the game starts, behind a progress
/// bar, so no screen waits on the network once play begins.
///
/// KNI's own TitleContainer fetches one file at a time, each blocking until it
/// arrives, which is what made New Game and every level take 5-10 seconds on a
/// real server. This fetches them all at once, keeps them in memory, and
/// becomes the TitleContainer, so the game reads from memory instead.
///
/// A file that fails here is fetched the old way when the game asks for it,
/// and reported then if it is really missing. A file that is not on the list
/// at all does not exist, and is answered as such without asking the server:
/// the game checks for a lot of art that was never drawn.
/// </summary>
public class ContentPreload : TitleContainerFactory
{
    /// <summary>How many downloads run at once.</summary>
    private const int Parallel = 8;

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    /// <param name="progress">Files done, files in all, bytes so far. Called after each file.</param>
    public async Task LoadAsync(HttpClient http, Action<int, int, long> progress)
    {
        var paths = WebContentIndex.AllPaths;
        int done = 0;
        long bytes = 0;
        using var gate = new SemaphoreSlim(Parallel);
        await Task.WhenAll(paths.Select(async path =>
        {
            await gate.WaitAsync();
            try
            {
                // "Florida Man" -> "Florida%20Man", one folder at a time
                string url = string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
                byte[] data = await http.GetByteArrayAsync(url);
                _files[path] = data;
                bytes += data.Length;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[preload] {path}: {ex.Message}");
            }
            finally
            {
                gate.Release();
                progress(++done, paths.Count, bytes);
            }
        }));
    }

    public override TitleContainerStrategy CreateTitleContainerStrategy() =>
        new FromMemory(_files, new ConcreteTitleContainerFactory().CreateTitleContainerStrategy());

    private static readonly HashSet<string> Listed = new(WebContentIndex.AllPaths, StringComparer.Ordinal);

    private sealed class FromMemory : TitleContainerStrategy
    {
        private readonly Dictionary<string, byte[]> _files;
        private readonly TitleContainerStrategy _network;

        public FromMemory(Dictionary<string, byte[]> files, TitleContainerStrategy network)
        {
            _files = files;
            _network = network;
        }

        public override string Location => _network.Location;
        public override Microsoft.Xna.Framework.TitlePlatform Platform => _network.Platform;

        public override Stream PlatformOpenStream(string name)
        {
            string path = name.Replace('\\', '/');
            if (_files.TryGetValue(path, out var data)) return new MemoryStream(data, writable: false);
            // built content (the font) is not on the list; nor is a file whose download failed
            if (path.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase) || Listed.Contains(path))
                return _network.PlatformOpenStream(name);
            throw new FileNotFoundException(path);
        }
    }
}
