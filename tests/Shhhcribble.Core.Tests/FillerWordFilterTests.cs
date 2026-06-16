using Shhhcribble.Core.Text;
using Xunit;

namespace Shhhcribble.Core.Tests;

// Ported 1:1 from the macOS app's FillerWordFilterTests (XCTest).
public class FillerWordFilterTests
{
    [Fact]
    public void RemovesUm() =>
        Assert.Equal("I think so", FillerWordFilter.Filter("I um think so"));

    [Fact]
    public void RemovesUhAndCollapsesWhitespace() =>
        Assert.Equal("Well maybe", FillerWordFilter.Filter("Well uh maybe"));

    [Fact]
    public void RemovesParentheticalYouKnow() =>
        Assert.Equal("I think", FillerWordFilter.Filter("I, you know, think"));

    [Fact]
    public void RemovesLeadingLike() =>
        Assert.Equal("that works", FillerWordFilter.Filter("Like, that works"));

    // Whole-word boundaries: words that merely contain a filler substring stay intact.
    [Fact]
    public void PreservesWordsContainingFillerSubstrings() =>
        Assert.Equal("An umbrella under the umbrella",
            FillerWordFilter.Filter("An umbrella under the umbrella"));

    [Fact]
    public void EmptyStaysEmpty() =>
        Assert.Equal("", FillerWordFilter.Filter(""));
}
