using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Kasi.Ipc.Messages;

namespace Kasi.Ipc.Tests;

[Trait("TC", "TC-BRW-006")]
[Trait("Requirement", "KASI-NFR-010")]
public class FixtureTests
{
    public static TheoryData<string> Valid => IpcFixtures.Valid;

    public static TheoryData<string> Invalid => IpcFixtures.Invalid;

    [Theory]
    [MemberData(nameof(Valid))]
    public void A_valid_fixture_parses_and_round_trips_to_an_equal_document(string name)
    {
        var bytes = IpcFixtures.Read("valid", name);

        var message = IpcSerializer.Deserialize(bytes);
        var written = IpcSerializer.Serialize(message);

        JsonNode.DeepEquals(JsonNode.Parse(written), JsonNode.Parse(bytes)).Should().BeTrue($"{name} should come back as it went in");
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void An_invalid_fixture_is_rejected_with_the_right_code_and_none_of_its_content(string name)
    {
        var bytes = IpcFixtures.Read("invalid", name);

        var act = () => IpcSerializer.Deserialize(bytes);

        var thrown = act.Should().Throw<IpcProtocolException>().Which;
        thrown.Code.Should().Be(name == "_--v-2" ? IpcErrorCode.UnsupportedVersion : IpcErrorCode.InvalidMessage);
        thrown.Message.Should().NotContain("s3cr3t").And.NotContain("example.com");
    }

    [Fact]
    public void Every_message_type_has_a_valid_fixture_and_an_invalid_one()
    {
        var types = typeof(IpcMessage).GetCustomAttributes<JsonDerivedTypeAttribute>().Select(a => (string)a.TypeDiscriminator!).ToList();
        var validTypes = IpcFixtures.NamesOf("valid").Select(n => JsonNode.Parse(IpcFixtures.Read("valid", n))!["type"]!.GetValue<string>()).ToHashSet();
        var invalidFor = IpcFixtures.NamesOf("invalid").Select(n => n.Split("--")[0]).ToHashSet();

        types.Should().HaveCount(11);
        types.Where(t => !validTypes.Contains(t)).Should().BeEmpty("each type needs a valid fixture");
        types.Where(t => !invalidFor.Contains(t)).Should().BeEmpty("each type needs a fixture named <type>--<reason> that is rejected");
    }

    [Fact]
    public void The_schema_oneOf_lists_every_message_type_once()
    {
        var types = typeof(IpcMessage).GetCustomAttributes<JsonDerivedTypeAttribute>().Select(a => (string)a.TypeDiscriminator!).ToList();
        using var schema = JsonDocument.Parse(File.ReadAllText(IpcFixtures.SchemaPath));
        var definitions = schema.RootElement.GetProperty("definitions");

        var oneOf = schema.RootElement.GetProperty("oneOf").EnumerateArray().Select(e => e.GetProperty("$ref").GetString()!.Split('/')[^1]).ToList();
        var discriminators = oneOf.Select(n => definitions.GetProperty(n).GetProperty("properties").GetProperty("type").GetProperty("const").GetString()).ToList();

        discriminators.Should().BeEquivalentTo(types).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_schema_caps_equal_the_ones_the_parser_enforces()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(IpcFixtures.SchemaPath));
        var d = schema.RootElement.GetProperty("definitions");

        d.GetProperty("webUrl").GetProperty("maxLength").GetInt32().Should().Be(IpcLimits.UrlLength);
        d.GetProperty("downloadUrl").GetProperty("maxLength").GetInt32().Should().Be(IpcLimits.UrlLength);
        d.GetProperty("id").GetProperty("maxLength").GetInt32().Should().Be(IpcLimits.IdLength);
        d.GetProperty("shortText").GetProperty("maxLength").GetInt32().Should().Be(IpcLimits.ShortText);
        d.GetProperty("textList").GetProperty("maxItems").GetInt32().Should().Be(IpcLimits.ListEntries);
        d.GetProperty("textList").GetProperty("items").GetProperty("maxLength").GetInt32().Should().Be(IpcLimits.ListEntryLength);
        d.GetProperty("cookies").GetProperty("maxItems").GetInt32().Should().Be(IpcLimits.Cookies);
        var cookie = d.GetProperty("cookie").GetProperty("properties");
        cookie.GetProperty("name").GetProperty("maxLength").GetInt32().Should().Be(IpcLimits.CookieNameLength);
        cookie.GetProperty("value").GetProperty("maxLength").GetInt32().Should().Be(IpcLimits.CookieValueLength);
        cookie.GetProperty("domain").GetProperty("maxLength").GetInt32().Should().Be(IpcLimits.CookieDomainLength);
        cookie.GetProperty("path").GetProperty("maxLength").GetInt32().Should().Be(IpcLimits.CookiePathLength);
        var add = d.GetProperty("addDownload").GetProperty("properties");
        add.GetProperty("filename").GetProperty("maxLength").GetInt32().Should().Be(IpcLimits.FileNameLength);
        add.GetProperty("mime").GetProperty("maxLength").GetInt32().Should().Be(IpcLimits.MimeLength);
        add.GetProperty("userAgent").GetProperty("maxLength").GetInt32().Should().Be(IpcLimits.UserAgentLength);
        d.GetProperty("addBatch").GetProperty("properties").GetProperty("links").GetProperty("maxItems").GetInt32().Should().Be(IpcLimits.BatchLinks);
        d.GetProperty("mediaDetected").GetProperty("properties").GetProperty("title").GetProperty("maxLength").GetInt32().Should().Be(IpcLimits.TitleLength);
        d.GetProperty("ack").GetProperty("properties").GetProperty("detail").GetProperty("maxLength").GetInt32().Should().Be(IpcLimits.DetailLength);
        var timeout = d.GetProperty("rules").GetProperty("properties").GetProperty("ackTimeoutMs");
        timeout.GetProperty("minimum").GetInt32().Should().Be(IpcLimits.AckTimeoutMinMs);
        timeout.GetProperty("maximum").GetInt32().Should().Be(IpcLimits.AckTimeoutMaxMs);
    }
}
