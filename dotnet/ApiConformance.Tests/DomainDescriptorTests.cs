using System.Text.Json;
using ApiConformance;

public class DomainDescriptorTests
{
    private const string Json = """
        {
          "name": "blog",
          "collectionRoute": "/api/blogs",
          "writableFields": [
            { "name": "name", "jsonType": "string", "clrType": "System.String", "samples": ["a", "b", "c"] }
          ],
          "serverOwnedFields": ["views"]
        }
        """;

    [Fact]
    public void GivenValidJson_WhenParsing_ThenFieldsAndSamplesLoad()
    {
        var domain = DomainDescriptor.Parse(Json);

        Assert.Equal("/api/blogs", domain.CollectionRoute);
        Assert.Null(domain.Parent);
        Assert.Equal(3, domain.WritableFields.Single().Samples.Count);
        Assert.Equal(["views"], domain.ServerOwnedFields);
    }

    [Fact]
    public void GivenDescriptor_WhenRoundTripped_ThenSamplesSurvive()
    {
        var domain = DomainDescriptor.Parse(Json);

        var again = DomainDescriptor.Parse(domain.ToJson());

        Assert.Equal(domain.WritableFields[0].Samples.Select(s => s.GetRawText()), again.WritableFields[0].Samples.Select(s => s.GetRawText()));
    }

    [Fact]
    public void GivenFewerThanThreeDistinctSamples_WhenParsing_ThenRejected()
    {
        var json = Json.Replace("[\"a\", \"b\", \"c\"]", "[\"a\", \"a\", \"b\"]");

        var failure = Assert.Throws<JsonException>(() => DomainDescriptor.Parse(json));

        Assert.Contains("distinct samples", failure.Message);
    }
}
