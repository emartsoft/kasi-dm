#pragma warning disable CA2000 // the test handler and client live for one test and own nothing unmanaged

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kasi.FaultInjection.Toxiproxy;

namespace Kasi.FaultInjection.Tests;

public sealed class ToxiproxyClientTests
{
    private sealed class Recorder(HttpStatusCode status = HttpStatusCode.OK, string body = "{}") : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, text));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static ToxiproxyClient ClientFor(Recorder recorder) =>
        new(new HttpClient(recorder) { BaseAddress = new Uri("http://127.0.0.1:8474/") });

    [Fact]
    public async Task Creating_a_proxy_posts_its_name_listen_and_upstream_and_returns_the_address_it_really_listens_on()
    {
        var recorder = new Recorder(body: """{"name":"p1","listen":"127.0.0.1:41234","upstream":"127.0.0.1:5000","enabled":true}""");

        var listen = await ClientFor(recorder).CreateProxyAsync("p1", "127.0.0.1:0", "127.0.0.1:5000", TestContext.Current.CancellationToken);

        listen.Should().Be("127.0.0.1:41234");
        ToxiproxyClient.PortOf(listen).Should().Be(41234);
        var (method, path, body) = recorder.Requests.Should().ContainSingle().Subject;
        method.Should().Be(HttpMethod.Post);
        path.Should().Be("/proxies");
        var json = JsonNode.Parse(body)!;
        ((string?)json["name"]).Should().Be("p1");
        ((string?)json["listen"]).Should().Be("127.0.0.1:0");
        ((string?)json["upstream"]).Should().Be("127.0.0.1:5000");
    }

    [Fact]
    public async Task A_toxic_is_posted_to_its_proxy_with_its_type_stream_and_attributes()
    {
        var recorder = new Recorder();

        await ClientFor(recorder).AddToxicAsync("p1", Toxic.Latency("lag", 300, 75), TestContext.Current.CancellationToken);
        await ClientFor(recorder).AddToxicAsync("p1", Toxic.Bandwidth("slow", 64), TestContext.Current.CancellationToken);
        await ClientFor(recorder).AddToxicAsync("p1", Toxic.Timeout("cut", 0), TestContext.Current.CancellationToken);

        recorder.Requests.Select(r => r.Path).Should().AllBe("/proxies/p1/toxics");
        var latency = JsonNode.Parse(recorder.Requests[0].Body)!;
        ((string?)latency["type"]).Should().Be("latency");
        ((string?)latency["stream"]).Should().Be("downstream");
        ((int?)latency["attributes"]!["latency"]).Should().Be(300);
        ((int?)latency["attributes"]!["jitter"]).Should().Be(75);
        ((int?)JsonNode.Parse(recorder.Requests[1].Body)!["attributes"]!["rate"]).Should().Be(64);
        ((int?)JsonNode.Parse(recorder.Requests[2].Body)!["attributes"]!["timeout"]).Should().Be(0);
    }

    [Fact]
    public async Task Removing_a_toxic_deleting_a_proxy_and_resetting_use_the_documented_routes()
    {
        var recorder = new Recorder();
        var client = ClientFor(recorder);

        await client.RemoveToxicAsync("p1", "slow", TestContext.Current.CancellationToken);
        await client.DeleteProxyAsync("p1", TestContext.Current.CancellationToken);
        await client.ResetAsync(TestContext.Current.CancellationToken);

        recorder.Requests.Select(r => (r.Method.Method, r.Path)).Should().Equal(
            ("DELETE", "/proxies/p1/toxics/slow"),
            ("DELETE", "/proxies/p1"),
            ("POST", "/reset"));
    }

    [Fact]
    public async Task A_refusal_becomes_an_exception_that_says_what_was_refused()
    {
        var recorder = new Recorder(HttpStatusCode.Conflict, """{"error":"proxy already exists"}""");

        var act = () => ClientFor(recorder).CreateProxyAsync("p1", "127.0.0.1:0", "127.0.0.1:5000", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("create proxy 'p1'").And.Contain("409").And.Contain("already exists");
    }
}
