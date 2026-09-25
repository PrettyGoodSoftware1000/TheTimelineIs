using System;
using System.IO;

namespace TheTimelineIs.Desktop;

/// <summary>
/// Where this game keeps the player's own files: the save, the content log,
/// and replays when there is no repo to put them in.
///
/// Every one of those used to fall back to the folder the executable is in,
/// which is fine for a build you run out of bin/ and wrong for a shipped one.
/// A Mac app lives in /Applications, which the player cannot write to, so the
/// save would simply never be written — and nothing would say so.
///
/// On a Mac that place is Application Support, not the XDG folder .NET
/// otherwise picks. ~/.config is hidden in the Finder; somebody who wants to
/// clear their save is never going to find it.
/// </summary>
public static class UserData
{
    public static string Folder { get; } = Build();

    /// <summary>One file inside it. The folder is made if it isn't there.</summary>
    public static string Path(string name)
    {
        Directory.CreateDirectory(Folder);
        return System.IO.Path.Combine(Folder, name);
    }

    private static string Build()
    {
        string home = OperatingSystem.IsMacOS()
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(home, "TheTimelineIs");
    }
}
