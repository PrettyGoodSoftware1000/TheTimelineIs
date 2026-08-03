using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TheTimelineIs.Core.Data;

public enum Severity { Warning, Error }

/// <summary>One content problem, pinned to the file and line that caused it.</summary>
public record Diagnostic(Severity Severity, string Source, int Line, string Message)
{
    /// <summary>Formatted the way editors expect, so the location is clickable.</summary>
    public override string ToString() =>
        Line > 0
            ? $"{Severity.ToString().ToUpperInvariant()}  {Source}:{Line}  {Message}"
            : $"{Severity.ToString().ToUpperInvariant()}  {Source}  {Message}";
}

/// <summary>
/// Collects every complaint the content loaders and the validator raise, so
/// they can be shown in one popup and written to one log instead of scrolling
/// past in a console nobody is watching.
///
/// A single shared instance is used deliberately: the loaders are static and
/// run once at startup, and threading one of these through every signature
/// bought nothing but noise.
/// </summary>
public class Diagnostics
{
    private readonly List<Diagnostic> _items = new();

    /// <summary>Swapped in at startup; the default silently discards, so tests and tools stay quiet.</summary>
    public static Diagnostics Current { get; set; } = new();

    public IReadOnlyList<Diagnostic> Items => _items;
    public bool HasErrors => _items.Any(i => i.Severity == Severity.Error);
    public bool Any => _items.Count > 0;
    public int ErrorCount => _items.Count(i => i.Severity == Severity.Error);
    public int WarningCount => _items.Count(i => i.Severity == Severity.Warning);

    public void Error(string source, int line, string message) =>
        Add(new Diagnostic(Severity.Error, source, line, message));

    public void Warn(string source, int line, string message) =>
        Add(new Diagnostic(Severity.Warning, source, line, message));

    private void Add(Diagnostic d)
    {
        // a repeated complaint about the same line is noise, not new information
        if (_items.Any(i => i.Source == d.Source && i.Line == d.Line && i.Message == d.Message))
            return;
        _items.Add(d);
        Console.WriteLine(d.ToString());
    }

    public void Clear() => _items.Clear();

    /// <summary>Errors first, then warnings; each group in the order found.</summary>
    public List<Diagnostic> Ordered() =>
        _items.OrderBy(i => i.Severity == Severity.Error ? 0 : 1).ToList();

    public string RenderLog()
    {
        var sb = new StringBuilder();
        sb.AppendLine("The Timeline Is — content check");
        sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine($"{ErrorCount} error(s), {WarningCount} warning(s)");
        sb.AppendLine(new string('-', 72));
        if (_items.Count == 0)
            sb.AppendLine("No problems found.");
        foreach (var d in Ordered())
            sb.AppendLine(d.ToString());
        return sb.ToString();
    }
}
