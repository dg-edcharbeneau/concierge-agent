using Concierge.Contracts;
using Concierge.WorkflowServer.Activities;
using Concierge.WorkflowServer.Setup;
using Npgsql;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace Concierge.Core.Tests;

/// <summary>
/// Session activities are started directly by Web as Standalone Activities, so they are the
/// worker-side input boundary formerly enforced by the session workflow's request contract.
/// </summary>
public sealed class ConciergeSessionActivityTests
{
    private static readonly byte[] ValidHash = new byte[32];

    public static TheoryData<VerifyConciergeSessionActivityRequest?> InvalidVerifyRequests => new()
    {
        null,
        new(Guid.Empty, ValidHash, "Rivera", "412"),
        new(Guid.NewGuid(), null!, "Rivera", "412"),
        new(Guid.NewGuid(), new byte[31], "Rivera", "412"),
        new(Guid.NewGuid(), ValidHash, null!, "412"),
        new(Guid.NewGuid(), ValidHash, " ", "412"),
        new(Guid.NewGuid(), ValidHash, new string('x', 101), "412"),
        new(Guid.NewGuid(), ValidHash, "Rivera", null!),
        new(Guid.NewGuid(), ValidHash, "Rivera", "0412"),
        new(Guid.NewGuid(), ValidHash, "Rivera", "bad room"),
    };

    public static TheoryData<AuthenticateConciergeSessionActivityRequest?> InvalidAuthenticateRequests => new()
    {
        null,
        new(null!),
        new([]),
        new(new byte[31]),
    };

    public static TheoryData<InvalidateConciergeSessionActivityRequest?> InvalidInvalidateRequests => new()
    {
        null,
        new(Guid.Empty, Guid.NewGuid()),
        new(Guid.NewGuid(), Guid.Empty),
    };

    /// <summary>Malformed Verify input fails without retries and before any database access.</summary>
    [Theory]
    [MemberData(nameof(InvalidVerifyRequests))]
    public Task VerifyRejectsInvalidRequests(VerifyConciergeSessionActivityRequest? request) =>
        AssertInvalidRequestAsync(activities => activities.VerifyAsync(request!));

    /// <summary>Malformed Authenticate input fails without retries and before any database access.</summary>
    [Theory]
    [MemberData(nameof(InvalidAuthenticateRequests))]
    public Task AuthenticateRejectsInvalidRequests(AuthenticateConciergeSessionActivityRequest? request) =>
        AssertInvalidRequestAsync(activities => activities.AuthenticateAsync(request!));

    /// <summary>Malformed Invalidate input fails without retries and before any database access.</summary>
    [Theory]
    [MemberData(nameof(InvalidInvalidateRequests))]
    public Task InvalidateRejectsInvalidRequests(InvalidateConciergeSessionActivityRequest? request) =>
        AssertInvalidRequestAsync(activities => activities.InvalidateAsync(request!));

    /// <summary>Arbitrary remote error types are not diagnostic values, even when nested in safe exceptions.</summary>
    [Fact]
    public void DiagnosticsExcludeArbitraryRemoteErrorTypes()
    {
        var failure = new ApplicationFailureException(
            "secret provider message", "PrivateCredential 10.0.0.7");
        Assert.Equal("ApplicationFailureException", ConciergeDiagnostics.ExceptionTypeChain(failure));
    }

    private static async Task AssertInvalidRequestAsync(Func<ConciergeSessionActivities, Task> run)
    {
        // The data source is never opened: validation must reject the request first.
        await using var dataSource = NpgsqlDataSource.Create("Host=invalid.invalid;Database=unused");
        var activities = new ConciergeSessionActivities(new ConciergeSessionService(dataSource));
        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(() =>
            new ActivityEnvironment().RunAsync(() => run(activities)));
        Assert.Equal(ConciergeActivityNames.SessionInvalidRequestErrorType, failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Equal("Invalid session request.", failure.Message);
    }
}
