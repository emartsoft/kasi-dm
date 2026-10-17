using System.Net;
using Kasi.TestServer;
using Kasi.TestServer.Endpoints;

namespace Kasi.Engine.Tests;

/// <summary>The same cases as <see cref="DispositionEndpointFileNameTests"/>, but through a real HTTP response, so the header decoding path is exercised.</summary>
[Trait("Category", "Integration")]
[Trait("TC", "TC-ENG-004")]
[Trait("TC", "TC-ENG-005")]
[Trait("Requirement", "KASI-ENG-003")]
public sealed class DispositionEndpointLiveTests : IAsyncLifetime
{
    public static TheoryData<string> CaseNames => new(DispositionEndpoints.Cases.Keys);

    private TestServerHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Resolves_expected_filename_from_live_test_server_disposition_endpoint(string caseName)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = new HttpClient { BaseAddress = _host.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };

        using var response = await client.GetAsync($"/disposition/{caseName}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rawHeaders = response.Content.Headers.GetValues("Content-Disposition");
        var resolvedFromList = DownloadFileName.Resolve(response.RequestMessage!.RequestUri, rawHeaders);
        var resolvedFromSingle = DownloadFileName.Resolve(response.RequestMessage.RequestUri, rawHeaders.Single());

        resolvedFromList.Should().Be(DispositionEndpointFileNameTests.ExpectedByCase[caseName]);
        resolvedFromSingle.Should().Be(DispositionEndpointFileNameTests.ExpectedByCase[caseName]);
        FileNameSanitiserTests.AssertStaysInTargetFolder(resolvedFromList);
    }
}
