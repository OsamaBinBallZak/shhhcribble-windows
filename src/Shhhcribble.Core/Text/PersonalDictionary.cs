using System.Text.RegularExpressions;

namespace Shhhcribble.Core.Text;

/// <summary>
/// Applies the user's personal dictionary to a transcript.
///
/// Semantics (load-bearing — pinned by <c>PersonalDictionaryTests</c>, ported
/// 1:1 from the macOS app):
/// <list type="bullet">
/// <item>Entries apply <b>sequentially in array order</b>; each entry operates
/// on the previous entry's output, exactly once. Ordering <i>is</i> the overlap
/// resolution: earlier entries win on the spans they rewrite, and a later entry
/// may legally match text an earlier replacement produced.</item>
/// <item><b>Whole-word</b> matching via lookarounds (<c>(?&lt;!\w) … (?!\w)</c>)
/// rather than <c>\b</c>, so phrases that start or end with non-word characters
/// ("C++", ".NET") still anchor correctly. Multi-word phrases need no special
/// handling — escaped spaces match literally.</item>
/// <item><c>CaseSensitive == false</c> matches any casing; the <b>replacement is
/// always used verbatim</b> (no smart-case adaption) because the dominant use
/// case is fixing proper nouns where the replacement's own casing is the point.</item>
/// </list>
/// </summary>
public static class PersonalDictionary
{
    public static string Apply(IReadOnlyList<DictionaryEntry> entries, string text)
    {
        if (string.IsNullOrEmpty(text) || entries.Count == 0) return text;

        var s = text;
        foreach (var entry in entries)
        {
            var phrase = entry.Phrase.Trim();
            if (phrase.Length == 0) continue;

            var pattern = @"(?<!\w)" + Regex.Escape(phrase) + @"(?!\w)";
            var options = entry.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;

            // The replacement is used verbatim. A MatchEvaluator returns the raw
            // string so regex template metacharacters ($1, $$, …) in the
            // replacement are never interpreted — the .NET equivalent of Swift's
            // escapedTemplate(for:).
            s = Regex.Replace(s, pattern, _ => entry.Replacement, options);
        }
        return s;
    }
}
