namespace TheTimelineIs.Core.Data;

/// <summary>
/// Word processors love smart quotes; the baked font only has ASCII glyphs.
/// Every parser runs authored text through this so 'Mancer and ’Mancer match.
/// </summary>
public static class TextUtil
{
    public static string Clean(string s) => s
        .Replace('‘', '\'').Replace('’', '\'')
        .Replace('“', '"').Replace('”', '"')
        .Replace('–', '-').Replace('—', '-')
        .Trim();
}
