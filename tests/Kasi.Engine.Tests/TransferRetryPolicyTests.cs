using System.Net;

namespace Kasi.Engine.Tests;

[Trait("Requirement", "KASI-RES-004")]
public sealed class TransferRetryPolicyTests
{
    private static TransferRetryPolicy Policy() => new()
    {
        Jitter = cap => cap,
    };

    [Fact]
    public void Transient_and_protocol_failures_retry_with_the_capped_full_jitter()
    {
        var policy = Policy();

        var transient = policy.Decide(TransferErrorKind.TransientNetworkError, failedAttempts: 1);
        var truncated = policy.Decide(TransferErrorKind.ProtocolError, failedAttempts: 2);

        transient.Retry.Should().BeTrue();
        transient.Delay.Should().Be(TimeSpan.FromSeconds(1));
        truncated.Retry.Should().BeTrue();
        truncated.Delay.Should().Be(TimeSpan.FromSeconds(2));
        truncated.ReduceConnections.Should().BeFalse();
    }

    [Fact]
    public void Retry_after_raises_the_delay_and_a_throttled_decision_reduces_connections()
    {
        var decision = Policy().Decide(
            TransferErrorKind.HttpStatusError,
            HttpStatusCode.TooManyRequests,
            TimeSpan.FromSeconds(5));

        decision.Retry.Should().BeTrue();
        decision.Delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
        decision.ReduceConnections.Should().BeTrue();
    }

    [Fact]
    public void Permanent_auth_file_changed_and_disk_failures_do_not_retry()
    {
        var policy = Policy();

        policy.Decide(TransferErrorKind.HttpStatusError, HttpStatusCode.NotFound).Retry.Should().BeFalse();
        policy.Decide(TransferErrorKind.HttpStatusError, HttpStatusCode.Unauthorized).Retry.Should().BeFalse();
        policy.Decide(TransferErrorKind.FileChanged).Retry.Should().BeFalse();
        policy.Decide(TransferErrorKind.DiskNotEnoughSpace).Retry.Should().BeFalse();
        policy.Decide(TransferErrorKind.DiskError).Retry.Should().BeFalse();
        policy.Decide(TransferErrorKind.Offline).Retry.Should().BeFalse();
    }

    [Fact]
    public void A_503_without_retry_after_is_transient_but_503_with_one_is_throttled()
    {
        var policy = Policy();

        policy.Decide(TransferErrorKind.HttpStatusError, HttpStatusCode.ServiceUnavailable).ReduceConnections.Should().BeFalse();
        policy.Decide(
            TransferErrorKind.HttpStatusError,
            HttpStatusCode.ServiceUnavailable,
            TimeSpan.FromSeconds(2)).ReduceConnections.Should().BeTrue();
    }

    [Fact]
    public void Max_attempts_stops_the_retry()
    {
        var policy = Policy() with { MaxAttempts = 3 };

        policy.Decide(TransferErrorKind.TransientNetworkError, failedAttempts: 2).Retry.Should().BeTrue();
        policy.Decide(TransferErrorKind.TransientNetworkError, failedAttempts: 3).Retry.Should().BeFalse();
    }

    [Fact]
    public void Full_jitter_stays_inside_the_cap()
    {
        var zero = new TransferRetryPolicy { Jitter = _ => TimeSpan.Zero };
        var beyond = new TransferRetryPolicy { Jitter = _ => TimeSpan.FromMinutes(5) };

        zero.DelayFor(1).Should().Be(TimeSpan.Zero);
        beyond.DelayFor(1).Should().Be(TimeSpan.FromSeconds(1));
    }
}
