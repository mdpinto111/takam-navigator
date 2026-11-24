using Takam.Core.Text;

namespace Takam.Tests;

public class HebrewTextTests
{
    [Theory]
    [InlineData("תכ\"ם", "תכם")]
    [InlineData("תכ״ם", "תכם")]       // gershayim
    [InlineData("תכ''ם", "תכם")]      // two apostrophes, common in the .docx files
    [InlineData("מט\"ח", "מטח")]
    public void Abbreviation_quotes_are_unified(string input, string expected) =>
        Assert.Equal(expected, HebrewText.Normalize(input));

    [Fact]
    public void Niqqud_and_direction_marks_are_removed() =>
        Assert.Equal("תשלום", HebrewText.Normalize("‏תַּשְׁלוּם‎"));

    [Fact]
    public void Maqaf_and_spaces_are_normalized() =>
        Assert.Equal("בין-משרדיים א", HebrewText.Normalize("בין־משרדיים    א"));

    [Fact]
    public void Variants_strip_up_to_three_prefix_letters() =>
        Assert.Equal(["והתשלום", "התשלום", "תשלום"], HebrewText.Variants("והתשלום"));

    [Fact]
    public void Variants_keep_a_stem_of_at_least_three_letters()
    {
        Assert.Equal(["בית"], HebrewText.Variants("בית"));   // "ית" would be too short
        Assert.DoesNotContain("ספ", HebrewText.Variants("לספ"));
    }

    [Fact]
    public void Non_hebrew_tokens_have_no_variants() =>
        Assert.Equal(["1.4.3"], HebrewText.Variants("1.4.3"));

    [Fact]
    public void Query_terms_drop_stop_words_and_duplicates() =>
        Assert.Equal(["ימים", "לשלם", "לספק"], HebrewText.QueryTerms("תוך כמה ימים צריך לשלם לספק? לספק"));

    [Fact]
    public void Decimal_numbers_stay_whole() =>
        Assert.Contains("1.4.5", HebrewText.Tokenize("ראה הוראה 1.4.5 להלן"));

    [Fact]
    public void Empty_input_gives_no_terms()
    {
        Assert.Equal("", HebrewText.Normalize("   "));
        Assert.Empty(HebrewText.QueryTerms(null));
        Assert.Empty(HebrewText.QueryTerms("מה האם של"));     // stop words only
    }

    [Fact]
    public void Index_text_contains_prefixless_forms() =>
        Assert.Contains("תשלום", HebrewText.ToIndexText("בתשלום").Split(' '));
}
