using System.Text.Json;
using Shhhcribble.Core.Text;
using Xunit;

namespace Shhhcribble.Core.Tests;

// Ported 1:1 from the macOS app's PersonalDictionaryTests (XCTest).
public class PersonalDictionaryTests
{
    private static DictionaryEntry E(string phrase, string replacement, bool caseSensitive = false) =>
        new(phrase, replacement, caseSensitive);

    [Fact]
    public void BasicReplacement() =>
        Assert.Equal("i met Parakeet today",
            PersonalDictionary.Apply(new[] { E("parakeet", "Parakeet") }, "i met parakeet today"));

    // Whole-word boundaries: phrases never match inside larger words.
    [Fact]
    public void WholeWordDoesNotMatchSubstring() =>
        Assert.Equal("the catalog lists a dog",
            PersonalDictionary.Apply(new[] { E("cat", "dog") }, "the catalog lists a cat"));

    [Fact]
    public void CaseInsensitiveMatchesAnyCaseAndReplacesVerbatim()
    {
        var entries = new[] { E("swiss borg", "SwissBorg") };
        Assert.Equal("SwissBorg is great", PersonalDictionary.Apply(entries, "Swiss Borg is great"));
        Assert.Equal("SwissBorg is great", PersonalDictionary.Apply(entries, "SWISS BORG is great"));
        Assert.Equal("SwissBorg is great", PersonalDictionary.Apply(entries, "swiss borg is great"));
    }

    [Fact]
    public void CaseSensitiveRequiresExactCase()
    {
        var entries = new[] { E("hendri", "Hendri", caseSensitive: true) };
        Assert.Equal("ask Hendri", PersonalDictionary.Apply(entries, "ask hendri"));
        Assert.Equal("ask Hendri", PersonalDictionary.Apply(entries, "ask Hendri"));
        Assert.Equal("ask HENDRI", PersonalDictionary.Apply(entries, "ask HENDRI"));
    }

    [Fact]
    public void MultiWordPhraseMidSentence() =>
        Assert.Equal("we use FluidAudio for transcription",
            PersonalDictionary.Apply(new[] { E("fluid audio", "FluidAudio") },
                "we use fluid audio for transcription"));

    [Fact]
    public void PunctuationAdjacentMatchesArePreserved() =>
        Assert.Equal("Parakeet, Parakeet. (Parakeet)",
            PersonalDictionary.Apply(new[] { E("parakeet", "Parakeet") },
                "parakeet, parakeet. (parakeet)"));

    // Entries apply sequentially in array order — entry 2 sees entry 1's output.
    [Fact]
    public void EntriesApplyInOrderAndCanChain()
    {
        Assert.Equal("baz",
            PersonalDictionary.Apply(new[] { E("foo", "bar"), E("bar", "baz") }, "foo"));
        Assert.Equal("bar",
            PersonalDictionary.Apply(new[] { E("bar", "baz"), E("foo", "bar") }, "foo"));
    }

    [Fact]
    public void EmptyDictionaryIsNoOp() =>
        Assert.Equal("hello there", PersonalDictionary.Apply(Array.Empty<DictionaryEntry>(), "hello there"));

    [Fact]
    public void EmptyPhraseEntryIsIgnored() =>
        Assert.Equal("hello there", PersonalDictionary.Apply(new[] { E("  ", "nope") }, "hello there"));

    // Regex metacharacters in the phrase and template metacharacters ($) in the
    // replacement are treated literally. Also exercises lookaround boundaries on
    // a phrase that ends in non-word characters ("c++").
    [Fact]
    public void RegexSpecialCharactersAreTreatedLiterally()
    {
        Assert.Equal("i write C++ daily",
            PersonalDictionary.Apply(new[] { E("c++", "C++") }, "i write c++ daily"));
        Assert.Equal("one $100",
            PersonalDictionary.Apply(new[] { E("buck", "$100") }, "one buck"));
    }

    [Fact]
    public void NoMatchReturnsInputUnchanged() =>
        Assert.Equal("no animals here",
            PersonalDictionary.Apply(new[] { E("zebra", "Zebra") }, "no animals here"));

    [Fact]
    public void EmptyTextStaysEmpty() =>
        Assert.Equal("", PersonalDictionary.Apply(new[] { E("a", "b") }, ""));

    // Stored JSON missing optional keys (id, caseSensitive) must still decode —
    // one strict-decode failure would wipe the whole dictionary.
    [Fact]
    public void DecodingToleratesMissingOptionalKeys()
    {
        const string json = """[{"phrase": "foo", "replacement": "bar"}]""";
        var decoded = JsonSerializer.Deserialize<List<DictionaryEntry>>(json)!;
        Assert.Single(decoded);
        Assert.Equal("foo", decoded[0].Phrase);
        Assert.Equal("bar", decoded[0].Replacement);
        Assert.False(decoded[0].CaseSensitive);
    }

    // Leading non-word characters also anchor correctly (the ".NET" case).
    [Fact]
    public void PhraseWithLeadingNonWordCharacter()
    {
        Assert.Equal("we use .NET here",
            PersonalDictionary.Apply(new[] { E("dot net", ".NET") }, "we use dot net here"));
        Assert.Equal("i like .NET a lot",
            PersonalDictionary.Apply(new[] { E(".net", ".NET") }, "i like .net a lot"));
    }
}
