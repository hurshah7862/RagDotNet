using RagDotNet.Keyword;

namespace RagDotNet.Core.Tests.Hybrid;

public class KeywordTokenizerTests
{
    [Fact]
    public void Version_numbers_are_indexed_whole_and_by_part()
    {
        var terms = KeywordTokenizer.Terms("Upgrade to v2.3.1 now");

        Assert.Contains("v2.3.1", terms);
        Assert.Contains("v2", terms);
        Assert.Contains("3", terms);
        Assert.Contains("1", terms);
        Assert.Contains("upgrade", terms);
        Assert.DoesNotContain("to", terms);
    }

    [Fact]
    public void Error_codes_and_setting_names_are_indexed_whole_and_by_part()
    {
        var terms = KeywordTokenizer.Terms("ERR-404 max_tokens");

        Assert.Equal(["err-404", "err", "404", "max_tokens", "max", "tokens"], terms);
    }

    [Fact]
    public void Stop_words_are_dropped()
    {
        Assert.Empty(KeywordTokenizer.Terms("The and of"));
    }

    [Fact]
    public void Punctuation_alone_gives_no_terms()
    {
        Assert.Empty(KeywordTokenizer.Terms("?!"));
        Assert.Empty(KeywordTokenizer.Terms("-._"));
    }

    [Fact]
    public void Unicode_letters_are_lowercased_invariantly()
    {
        Assert.Equal(["größe", "ünïcode"], KeywordTokenizer.Terms("Größe Ünïcode"));
    }

    [Fact]
    public void No_stemming()
    {
        Assert.Equal(["notes"], KeywordTokenizer.Terms("notes"));
    }

    [Fact]
    public void Trailing_joiners_are_not_part_of_a_term()
    {
        Assert.Equal(["end", "v2"], KeywordTokenizer.Terms("end. v2-"));
    }
}
