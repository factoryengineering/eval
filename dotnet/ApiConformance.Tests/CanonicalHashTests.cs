using ApiConformance;

public class CanonicalHashTests
{
    private const string Id = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string ParentId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private static KeyValuePair<string, object> Field(string name, object value) => new(name, value);

    [Fact]
    public void GivenIdAndName_WhenHashing_ThenMatchesIndependentVector()
    {
        var hash = CanonicalHash.Compute([Field("id", Guid.Parse(Id)), Field("name", "gear")]);

        Assert.Equal("E8nskwEfrGHFO_eGiOO2klena6fnstJc9R7PwfQ4AJI", hash);
    }

    [Fact]
    public void GivenEscapesAndDecomposedLetters_WhenHashing_ThenNfcAndEscapingMatchIndependentVector()
    {
        var decomposed = "cafe\u0301 \"q\" \\ \n \u0001";

        var hash = CanonicalHash.Compute([Field("id", Guid.Parse(Id)), Field("blogId", Guid.Parse(ParentId)), Field("title", decomposed)]);

        Assert.Equal("h-Pwti8BwsQZJPGsLfDEe0NRp2ZWZ7GvYmy1WllIxj0", hash);
    }

    [Fact]
    public void GivenComposedAndDecomposedStrings_WhenHashing_ThenHashesAreEqual()
    {
        var composed = CanonicalHash.Compute([Field("name", "caf\u00e9")]);
        var decomposed = CanonicalHash.Compute([Field("name", "cafe\u0301")]);

        Assert.Equal(composed, decomposed);
    }

    [Fact]
    public void GivenNumberAndBoolean_WhenHashing_ThenMatchesIndependentVector()
    {
        var hash = CanonicalHash.Compute([Field("id", Guid.Parse(Id)), Field("title", "x"), Field("count", 5), Field("flag", true)]);

        Assert.Equal("fzZhOLggcOoRasGQT9QUZRIFgr0oicWY-siiZA_f4rY", hash);
    }

    [Fact]
    public void GivenSwappedFieldOrder_WhenHashing_ThenMatchesDifferentIndependentVector()
    {
        var hash = CanonicalHash.Compute([Field("name", "gear"), Field("id", Guid.Parse(Id))]);

        Assert.Equal("VwiNCmWRaoKcin7fMDptRWmqMYUo7Npe3pMO4TmYpRQ", hash);
        Assert.NotEqual("E8nskwEfrGHFO_eGiOO2klena6fnstJc9R7PwfQ4AJI", hash);
    }

    [Fact]
    public void GivenUppercaseGuid_WhenCanonicalizing_ThenLowercaseDForm()
    {
        var json = CanonicalHash.Json([Field("id", Guid.Parse(Id.ToUpperInvariant()))]);

        Assert.Equal($$"""{"id":"{{Id}}"}""", json);
    }

    [Fact]
    public void GivenBoolIntAndStrings_WhenHashing_ThenMatchesIndependentVector()
    {
        var hash = CanonicalHash.Compute([Field("id", Guid.Parse(Id)), Field("subject", "printer"), Field("priority", 3), Field("escalated", false), Field("category", "hardware")]);

        Assert.Equal("gAPINFBMbGHqJg_k8PPr4DJ17lwoyhAxG6dP4XuoqWQ", hash);
    }

    [Fact]
    public void GivenTwoDecimalWeightAsJsonElement_WhenHashing_ThenTrailingZeroIsDroppedAndMatchesIndependentVector()
    {
        var weight = System.Text.Json.JsonDocument.Parse("12.50").RootElement;

        var hash = CanonicalHash.Compute([Field("id", Guid.Parse(Id)), Field("warehouseId", Guid.Parse(ParentId)), Field("reference", "SHP-1"), Field("weightKg", weight), Field("fragile", true)]);

        Assert.Equal("8ePTWBSJbE1BJ4_2KT4YLk8E5fQ2ekZCJBaSWrwoS3o", hash);
    }

    [Theory]
    [InlineData("100", "AZrFGZWOF5sjvSVuMl862ajEUHPxg5UAMxEvVGnLvfg")]
    [InlineData("100.00", "AZrFGZWOF5sjvSVuMl862ajEUHPxg5UAMxEvVGnLvfg")]
    [InlineData("0.05", "23WiMzJcVY3PuVva4kXQTcE3zaCfqGfqFd1GOp9VLTM")]
    public void GivenEquivalentNumberSpellings_WhenHashing_ThenMatchesIndependentVector(string spelling, string expected)
    {
        var number = System.Text.Json.JsonDocument.Parse(spelling).RootElement;

        var hash = CanonicalHash.Compute([Field("id", Guid.Parse(Id)), Field("weightKg", number)]);

        Assert.Equal(expected, hash);
    }
}
