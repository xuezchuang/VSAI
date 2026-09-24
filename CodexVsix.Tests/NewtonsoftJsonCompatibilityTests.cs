using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CodexVsix.Services;
using Xunit;

namespace CodexVsix.Tests;

public sealed class NewtonsoftJsonCompatibilityTests
{
    [Theory]
    [InlineData("2026-09-23T01:02:03Z")]
    [InlineData("2026-09-23T01:02:03.123456+08:00")]
    public void ProtocolPaginationPreservesDateShapedCursorsAndMessageText(string cursor)
    {
        var wire = new JObject { ["nextCursor"] = cursor, ["text"] = cursor }.ToString(Formatting.None);
        var value = NewtonsoftJsonCompatibility.ParseProtocolValue(wire);
        Assert.Equal(JTokenType.String, value["nextCursor"]!.Type);
        Assert.Equal(cursor, value["nextCursor"]!.Value<string>());
        Assert.Equal(cursor, value["text"]!.Value<string>());
        var nextRequest = new JObject { ["cursor"] = value["nextCursor"]!.Value<string>() };
        Assert.Equal("{\"cursor\":\"" + cursor + "\"}", NewtonsoftJsonCompatibility.Serialize(nextRequest, Formatting.None));
    }

    [Fact]
    public void SerializesCompactAndIndentedJsonAgainstTheVisualStudioCompatibleApi()
    {
        var token = JObject.Parse(@"{ ""value"": 1 }");

        var compact = NewtonsoftJsonCompatibility.Serialize(token, Formatting.None);
        var indented = NewtonsoftJsonCompatibility.Serialize(token, Formatting.Indented);

        Assert.Equal(@"{""value"":1}", compact);
        Assert.Contains("\"value\": 1", indented);
    }
}
