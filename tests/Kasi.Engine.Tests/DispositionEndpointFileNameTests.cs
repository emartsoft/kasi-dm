using System.Net;
using Kasi.TestServer;
using Kasi.TestServer.Endpoints;

namespace Kasi.Engine.Tests;

[Trait("TC", "TC-ENG-004")]
[Trait("TC", "TC-ENG-005")]
[Trait("Requirement", "KASI-ENG-003")]
public sealed class DispositionEndpointFileNameTests : IAsyncLifetime
{
    public static IReadOnlyDictionary<string, string> ExpectedByCase { get; } = new Dictionary<string, string>
    {
        ["rfc5987"] = "€ rates.txt",
        ["rfc5987-with-fallback"] = "€ rates.txt",
        ["traversal"] = "evil.txt",
        ["traversal-backslash"] = "evil.txt",
        ["traversal-encoded"] = "evil.txt",
        ["absolute-path"] = "hosts",
        ["reserved"] = "_CON.txt",
        ["reserved-bare"] = "_NUL",
        ["quoted-escapes"] = "a _quoted_ name.txt",
        ["semicolon"] = "a;b.txt",
        ["trailing-dots-spaces"] = "report.txt",
        ["invalid-chars"] = "a_b_c_d_e_f_.txt",
        ["long"] = new string('a', 196) + ".bin",
        ["empty"] = "empty",
        ["no-filename"] = "no-filename",
        ["inline"] = "inline.pdf",
    };

    public static TheoryData<string> CaseNames => new(DispositionEndpoints.Cases.Keys);

    private TestServerHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public void Expected_table_covers_every_disposition_endpoint_case_without_drift()
    {
        ExpectedByCase.Keys.Should().BeEquivalentTo(DispositionEndpoints.Cases.Keys);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Resolves_expected_filename_from_disposition_cases_in_memory(string caseName)
    {
        var header = DispositionEndpoints.Cases[caseName];
        var url = $"http://127.0.0.1/disposition/{caseName}";

        var resolved = DownloadFileName.Resolve(url, header);

        resolved.Should().Be(ExpectedByCase[caseName]);
        FileNameSanitiserTests.AssertStaysInTargetFolder(resolved);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    [Trait("Category", "Integration")]
    public async Task Resolves_expected_filename_from_live_test_server_disposition_endpoint(string caseName)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = new HttpClient { BaseAddress = _host.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };

        using var response = await client.GetAsync($"/disposition/{caseName}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rawHeaders = response.Content.Headers.GetValues("Content-Disposition");
        var resolvedFromList = DownloadFileName.Resolve(response.RequestMessage!.RequestUri, rawHeaders);
        var resolvedFromSingle = DownloadFileName.Resolve(response.RequestMessage.RequestUri, rawHeaders.Single());

        resolvedFromList.Should().Be(ExpectedByCase[caseName]);
        resolvedFromSingle.Should().Be(ExpectedByCase[caseName]);
        FileNameSanitiserTests.AssertStaysInTargetFolder(resolvedFromList);
    }
}
