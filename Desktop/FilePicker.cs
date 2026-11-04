using System;
using System.Diagnostics;
using System.IO;

namespace TheTimelineIs.Desktop;

/// <summary>
/// "Pick a file" using whatever the machine already has.
///
/// MonoGame has no file dialog and SDL2 has none either, so this asks the
/// desktop: PowerShell on Windows, osascript on a Mac, zenity or kdialog on
/// Linux. All three ship with the system or with the desktop the user is
/// already running.
///
/// Nothing here is required. When no dialog can be found — a bare Linux box,
/// a locked-down machine — it says so and the caller falls back to listing a
/// folder, which is what the editor did before this existed.
///
/// Desktop only, obviously.
/// </summary>
public static class FilePicker
{
    /// <summary>
    /// Asks for one file and returns its path, or null if the person
    /// cancelled or there is no dialog to ask with.
    /// </summary>
    /// <param name="title">Shown on the dialog.</param>
    /// <param name="extension">Without the dot, e.g. "zip".</param>
    /// <param name="startIn">Where to open. Ignored if it isn't there.</param>
    public static string? PickFile(string title, string extension, string? startIn = null)
    {
        if (startIn != null && !Directory.Exists(startIn)) startIn = null;
        try
        {
            string? path =
                OperatingSystem.IsWindows() ? Windows(title, extension, startIn)
                : OperatingSystem.IsMacOS() ? Mac(title, extension, startIn)
                : Linux(title, extension, startIn);
            path = path?.Trim();
            return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[picker] no file dialog available: {ex.Message}");
            return null;
        }
    }

    /// <summary>True when this machine has something that can show a dialog.</summary>
    public static bool Available =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ||
        Which("zenity") != null || Which("kdialog") != null;

    private static string? Windows(string title, string ext, string? dir)
    {
        // One line of PowerShell. -STA because the dialog is a WinForms
        // control and will not open on a multi-threaded apartment.
        string filter = $"{ext.ToUpperInvariant()} files (*.{ext})|*.{ext}|All files (*.*)|*.*";
        string script =
            "Add-Type -AssemblyName System.Windows.Forms; " +
            "$d = New-Object System.Windows.Forms.OpenFileDialog; " +
            $"$d.Title = '{Escape(title)}'; $d.Filter = '{filter}'; " +
            (dir != null ? $"$d.InitialDirectory = '{Escape(dir)}'; " : "") +
            "if ($d.ShowDialog() -eq 'OK') { $d.FileName }";
        return Run("powershell", new[] { "-NoProfile", "-STA", "-Command", script });
    }

    private static string Escape(string s) => s.Replace("'", "''");

    private static string? Mac(string title, string ext, string? dir)
    {
        string script =
            (dir != null ? $"set d to POSIX file \"{dir}\"\n" : "") +
            $"set f to choose file with prompt \"{title}\" of type {{\"{ext}\"}}" +
            (dir != null ? " default location d" : "") + "\n" +
            "POSIX path of f";
        return Run("osascript", new[] { "-e", script });
    }

    private static string? Linux(string title, string ext, string? dir)
    {
        if (Which("zenity") != null)
            return Run("zenity", new[]
            {
                "--file-selection", $"--title={title}",
                $"--file-filter={ext} files | *.{ext}", "--file-filter=All files | *",
                dir != null ? $"--filename={dir}{Path.DirectorySeparatorChar}" : "--filename=",
            });
        if (Which("kdialog") != null)
            return Run("kdialog", new[]
            {
                "--title", title,
                "--getopenfilename", dir ?? ".", $"*.{ext}|{ext} files",
            });
        throw new InvalidOperationException("install zenity or kdialog for a file dialog");
    }

    private static string? Which(string tool)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, tool);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? Run(string exe, string[] args)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);

        using var p = Process.Start(info);
        if (p == null) return null;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        // Cancel exits non-zero on every one of them, which is not a failure.
        return p.ExitCode == 0 ? output : null;
    }
}
