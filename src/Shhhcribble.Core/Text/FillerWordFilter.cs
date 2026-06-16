using System.Text.RegularExpressions;

namespace Shhhcribble.Core.Text;

/// <summary>
/// Strips unambiguous spoken filler words from transcribed text.
/// Conservative by design — only removes patterns that are unambiguously filler
/// to avoid corrupting legitimate sentences.
///
/// Ported 1:1 from the macOS app's <c>FillerWordFilter.swift</c>; semantics are
/// pinned by <c>FillerWordFilterTests</c>.
/// </summary>
public static class FillerWordFilter
{
    // 1. Definite fillers — safe to remove in all contexts.
    //    Each captures an optional trailing comma to avoid orphaned punctuation.
    private static readonly string[] Definite =
    {
        @"\b[Uu]m+h?\b,?",  // um, umm, umh
        @"\b[Uu]h+\b,?",    // uh, uhh
        @"\b[Hh]m+\b,?",    // hm, hmm
        @"\b[Ee]r+\b,?",    // er, err
        @"\b[Ee]rm+\b,?",   // erm
    };

    public static string Filter(string text)
    {
        var s = text;

        foreach (var pattern in Definite)
            s = Regex.Replace(s, pattern, " ");

        // 2. Contextual fillers — only removed when clearly parenthetical.

        // "you know" sandwiched by commas: "I, you know, think" -> "I think"
        s = Regex.Replace(s, @",\s*[Yy]ou know,\s*", " ");
        // "you know" at sentence end: "I think, you know." -> "I think."
        s = Regex.Replace(s, @",?\s*[Yy]ou know\.?$", "");
        // "You know, ..." at sentence start
        s = Regex.Replace(s, @"^[Yy]ou know,\s*", "");
        // "Like, ..." only at the very start of the text (clear filler opener)
        s = Regex.Replace(s, @"^[Ll]ike,\s+", "");

        // 3. Clean up artifacts left by removed words.
        s = Regex.Replace(s, @"\s{2,}", " ");
        s = s.Trim();
        // Remove leading comma/semicolon left by a stripped opener
        s = Regex.Replace(s, @"^[,;]+\s*", "");

        return s;
    }
}
