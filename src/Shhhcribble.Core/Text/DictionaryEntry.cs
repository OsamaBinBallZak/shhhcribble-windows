using System.Text.Json.Serialization;

namespace Shhhcribble.Core.Text;

/// <summary>
/// A single phrase→replacement substitution in the user's personal dictionary
/// (names, jargon — terms Parakeet reliably mishears).
///
/// JSON keys mirror the macOS app's Codable layout (<c>id</c>, <c>phrase</c>,
/// <c>replacement</c>, <c>caseSensitive</c>) so a dictionary exported from one
/// platform round-trips on the other. Decoding is tolerant: a stored entry
/// missing <c>id</c> or <c>caseSensitive</c> still loads, because the store
/// decodes all-or-nothing and one strict-decode failure would silently wipe the
/// whole dictionary.
/// </summary>
public sealed class DictionaryEntry
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("phrase")]
    public string Phrase { get; set; } = "";

    [JsonPropertyName("replacement")]
    public string Replacement { get; set; } = "";

    [JsonPropertyName("caseSensitive")]
    public bool CaseSensitive { get; set; }

    public DictionaryEntry() { }

    public DictionaryEntry(string phrase, string replacement, bool caseSensitive = false)
    {
        Phrase = phrase;
        Replacement = replacement;
        CaseSensitive = caseSensitive;
    }
}
