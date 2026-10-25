using Concierge.Contracts;
using Concierge.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Client;
using Temporalio.Client.Interceptors;
using Temporalio.Exceptions;
using Temporalio.Extensions.WorkflowStreams;

namespace Concierge.Core.Tests;

public sealed class ConciergeRideActivityServiceTests(PostgreSqlFixture postgreSql)
    : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task SnapshotReturnsOnlyNewest25AndKeepsEachVerifiedStayIsolated()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var firstStay = await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        var secondStay = await host.SeedStayAsync("Morgan", "812", today, today.AddDays(2));
        await using var scope = host.App.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IConciergeService>();
        var firstCredential = (await service.VerifyAsync(new("Rivera", "412"))).Credential;
        var secondCredential = (await service.VerifyAsync(new("Morgan", "812"))).Credential;

        // Authenticated snapshot reads create/join only the workflow bound to each credential.
        await service.ReadRideActivitySnapshotAsync(firstCredential);
        await service.ReadRideActivitySnapshotAsync(secondCredential);
        await PublishEntriesAsync(host, firstStay, 40, offsetBase: 0);
        await PublishEntriesAsync(host, secondStay, 3, offsetBase: 0);

        var firstSnapshot = await service.ReadRideActivitySnapshotAsync(firstCredential);
        var secondSnapshot = await service.ReadRideActivitySnapshotAsync(secondCredential);

        Assert.Equal(25, firstSnapshot.Entries.Count);
        Assert.Equal(Enumerable.Range(15, 25), firstSnapshot.Entries.Select(item => checked((int)item.Offset)));
        Assert.Equal(40, firstSnapshot.ReadState.SnapshotOffset);
        Assert.Equal(0, firstSnapshot.ReadState.ReadOffset);
        Assert.Equal(25, new ConciergeRideActivityWindow(firstSnapshot).UnreadCount);
        Assert.All(firstSnapshot.Entries, item => Assert.Equal(1, item.Entry.Sequence));

        Assert.Equal(3, secondSnapshot.Entries.Count);
        Assert.Equal(Enumerable.Range(0, 3), secondSnapshot.Entries.Select(item => checked((int)item.Offset)));
        Assert.Equal(3, secondSnapshot.ReadState.SnapshotOffset);
        Assert.Equal(3, new ConciergeRideActivityWindow(secondSnapshot).UnreadCount);
        Assert.DoesNotContain(
            firstSnapshot.Entries.Select(item => item.Entry.RideId),
            rideId => secondSnapshot.Entries.Any(item => item.Entry.RideId == rideId));
    }

    [Fact]
    public async Task MarkingDisplayedSnapshotLeavesLaterArrivalsUnreadAndReadCursorOnlyMovesForward()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var stayId = await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        await using var scope = host.App.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IConciergeService>();
        var credential = (await service.VerifyAsync(new("Rivera", "412"))).Credential;
        await service.ReadRideActivitySnapshotAsync(credential);
        await PublishEntriesAsync(host, stayId, 5, offsetBase: 0);

        var displayed = await service.ReadRideActivitySnapshotAsync(credential);
        Assert.Equal(5, displayed.ReadState.SnapshotOffset);
        await PublishEntriesAsync(host, stayId, 1, offsetBase: 5);

        var marked = await service.MarkRideActivitySnapshotReadAsync(
            credential,
            displayed.ReadState.SnapshotOffset);
        var afterMark = await service.ReadRideActivitySnapshotAsync(credential);
        var staleMark = await service.MarkRideActivitySnapshotReadAsync(credential, 2);

        Assert.Equal(5, marked.ReadOffset);
        Assert.Equal(5, afterMark.ReadState.ReadOffset);
        Assert.Equal(6, afterMark.ReadState.SnapshotOffset);
        Assert.Equal(1, new ConciergeRideActivityWindow(afterMark).UnreadCount);
        Assert.Equal(5, staleMark.ReadOffset);
    }

    [Fact]
    public async Task LiveSubscriptionStartsAtSnapshotBoundaryAndCancellationDisposesIt()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var stayId = await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        await using var scope = host.App.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IConciergeService>();
        var credential = (await service.VerifyAsync(new("Rivera", "412"))).Credential;
        var snapshot = await service.ReadRideActivitySnapshotAsync(credential);
        await PublishEntriesAsync(host, stayId, 1, offsetBase: snapshot.ReadState.SnapshotOffset);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var subscription = service
            .StreamRideActivityAsync(credential, snapshot.ReadState.SnapshotOffset, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(snapshot.ReadState.SnapshotOffset, subscription.Current.Offset);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task InvalidExpiredAndClosedStaySessionsFailExplicitly()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var stayId = await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        await using var scope = host.App.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IConciergeService>();

        var missing = await Assert.ThrowsAsync<ConciergeApiException>(
            () => service.ReadRideActivitySnapshotAsync("not-a-bearer"));
        Assert.Equal(StatusCodes.Status401Unauthorized, missing.StatusCode);
        var missingMark = await Assert.ThrowsAsync<ConciergeApiException>(
            () => service.MarkRideActivitySnapshotReadAsync("not-a-bearer", 0));
        Assert.Equal(StatusCodes.Status401Unauthorized, missingMark.StatusCode);
        await using (var missingStream = service.StreamRideActivityAsync("not-a-bearer", 0).GetAsyncEnumerator())
        {
            var streamFailure = await Assert.ThrowsAsync<ConciergeApiException>(
                () => missingStream.MoveNextAsync().AsTask());
            Assert.Equal(StatusCodes.Status401Unauthorized, streamFailure.StatusCode);
        }

        var credential = (await service.VerifyAsync(new("Rivera", "412"))).Credential;
        await host.ExecuteAsync(
            """
            UPDATE concierge_sessions
            SET expires_at_utc = CURRENT_TIMESTAMP - INTERVAL '1 second'
            WHERE stay_id = $1;
            """,
            stayId);
        var expired = await Assert.ThrowsAsync<ConciergeApiException>(
            () => service.ReadRideActivitySnapshotAsync(credential));
        Assert.Equal(StatusCodes.Status401Unauthorized, expired.StatusCode);
        var expiredMark = await Assert.ThrowsAsync<ConciergeApiException>(
            () => service.MarkRideActivitySnapshotReadAsync(credential, 0));
        Assert.Equal(StatusCodes.Status401Unauthorized, expiredMark.StatusCode);
        await using (var expiredStream = service.StreamRideActivityAsync(credential, 0).GetAsyncEnumerator())
        {
            var streamFailure = await Assert.ThrowsAsync<ConciergeApiException>(
                () => expiredStream.MoveNextAsync().AsTask());
            Assert.Equal(StatusCodes.Status401Unauthorized, streamFailure.StatusCode);
        }

        // Authentication is valid again only in this isolated test database. A closed workflow
        // must still surface as unavailable rather than an empty, success-shaped feed.
        await host.ExecuteAsync(
            "UPDATE concierge_sessions SET expires_at_utc = CURRENT_TIMESTAMP + INTERVAL '1 hour' WHERE stay_id = $1;",
            stayId);
        var restoredCredential = credential;
        var snapshot = await service.ReadRideActivitySnapshotAsync(restoredCredential);
        await host.Environment.Client.GetWorkflowHandle(
                ConciergeStayWorkflowIds.ForStay(stayId))
            .TerminateAsync("service-test cleanup");

        var failure = await Assert.ThrowsAsync<ConciergeApiException>(
            () => service.ReadRideActivitySnapshotAsync(restoredCredential));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, failure.StatusCode);
        Assert.Empty(snapshot.Entries);

        await using var closedStream = service
            .StreamRideActivityAsync(restoredCredential, snapshot.ReadState.SnapshotOffset)
            .GetAsyncEnumerator();
        var closedStreamFailure = await Assert.ThrowsAsync<ConciergeApiException>(
            () => closedStream.MoveNextAsync().AsTask());
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, closedStreamFailure.StatusCode);
    }

    [Fact]
    public async Task ReconnectRetriesTemporaryReauthenticationFailureAndResumesAtNextOffset()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var stayId = await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        await using var scope = host.App.Services.CreateAsyncScope();
        var credential = (await scope.ServiceProvider.GetRequiredService<IConciergeService>()
            .VerifyAsync(new("Rivera", "412"))).Credential;
        var snapshot = await scope.ServiceProvider.GetRequiredService<IConciergeService>()
            .ReadRideActivitySnapshotAsync(credential);
        var first = snapshot.ReadState.SnapshotOffset;
        await PublishEntriesAsync(host, stayId, 2, offsetBase: first);

        // The poll after both pre-published entries fails transiently, forcing a reconnect; the
        // first reauthentication then fails as Temporal-unavailable before succeeding.
        var faults = new RideStreamFaultInterceptor(failPollFromOffset: first + 2)
        {
            ReauthenticationFailures = 1,
        };
        var service = CreateServiceWithFaults(host, scope, faults);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var subscription = service
            .StreamRideActivityAsync(credential, first, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(first, subscription.Current.Offset);
        Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(first + 1, subscription.Current.Offset);

        await PublishEntriesAsync(host, stayId, 1, offsetBase: first + 2);
        Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(first + 2, subscription.Current.Offset);

        Assert.Equal(1, faults.PollFaults);
        Assert.Equal(1, faults.ReauthenticationFaults);
        // Initial authentication, the failed reauthentication, and the successful retry.
        Assert.Equal(3, faults.AuthenticateStarts);
    }

    [Fact]
    public async Task ReconnectWithExpiredSessionTerminatesUnauthorized()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var today = ConciergeTestHost.HotelToday;
        var stayId = await host.SeedStayAsync("Rivera", "412", today, today.AddDays(2));
        await using var scope = host.App.Services.CreateAsyncScope();
        var credential = (await scope.ServiceProvider.GetRequiredService<IConciergeService>()
            .VerifyAsync(new("Rivera", "412"))).Credential;
        var snapshot = await scope.ServiceProvider.GetRequiredService<IConciergeService>()
            .ReadRideActivitySnapshotAsync(credential);
        var first = snapshot.ReadState.SnapshotOffset;
        await PublishEntriesAsync(host, stayId, 1, offsetBase: first);

        // Expire the session exactly when the reconnect-forcing fault fires, so reauthentication
        // deterministically observes the expired credential.
        var faults = new RideStreamFaultInterceptor(failPollFromOffset: first + 1)
        {
            OnPollFault = () => host.ExecuteAsync(
                """
                UPDATE concierge_sessions
                SET expires_at_utc = CURRENT_TIMESTAMP - INTERVAL '1 second'
                WHERE stay_id = $1;
                """,
                stayId),
        };
        var service = CreateServiceWithFaults(host, scope, faults);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var subscription = service
            .StreamRideActivityAsync(credential, first, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(first, subscription.Current.Offset);
        var failure = await Assert.ThrowsAsync<ConciergeApiException>(
            () => subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(StatusCodes.Status401Unauthorized, failure.StatusCode);
        Assert.Equal(1, faults.PollFaults);
        Assert.Equal(2, faults.AuthenticateStarts);
    }

    private static ConciergeService CreateServiceWithFaults(
        ConciergeTestHost host,
        AsyncServiceScope scope,
        RideStreamFaultInterceptor faults)
    {
        var options = (TemporalClientOptions)host.Environment.Client.Options.Clone();
        options.Interceptors = [faults];
        var client = new TemporalClient(host.Environment.Client.Connection, options);
        return ActivatorUtilities.CreateInstance<ConciergeService>(
            scope.ServiceProvider,
            (ITemporalClient)client);
    }

    /// <summary>
    /// Injects one transient stream-poll failure at a chosen offset and optional transient
    /// failures for the reauthentication activity that follows it.
    /// </summary>
    private sealed class RideStreamFaultInterceptor(long failPollFromOffset) : IClientInterceptor
    {
        private readonly long failPollFromOffset = failPollFromOffset;
        private int pollFaults;
        private int reauthenticationAttempts;
        private int reauthenticationFaults;
        private int authenticateStarts;

        public int ReauthenticationFailures { get; init; }

        public Func<Task>? OnPollFault { get; init; }

        public int PollFaults => Volatile.Read(ref pollFaults);

        public int ReauthenticationFaults => Volatile.Read(ref reauthenticationFaults);

        public int AuthenticateStarts => Volatile.Read(ref authenticateStarts);

        public ClientOutboundInterceptor InterceptClient(ClientOutboundInterceptor nextInterceptor) =>
            new Outbound(this, nextInterceptor);

        // The stream's poll input type is internal to Temporalio.Extensions.WorkflowStreams, so
        // match it by its public wire shape instead of by type.
        private static bool IsPollFrom(object? argument, long offset) =>
            argument is not null
            && argument.GetType().Name == "PollInput"
            && argument.GetType().GetProperty("FromOffset")?.GetValue(argument) is { } fromOffset
            && Convert.ToInt64(fromOffset, System.Globalization.CultureInfo.InvariantCulture) == offset;

        private static RpcException Unavailable() =>
            new(RpcException.StatusCode.Unavailable, "injected transient failure", null);

        private sealed class Outbound(RideStreamFaultInterceptor owner, ClientOutboundInterceptor next)
            : ClientOutboundInterceptor(next)
        {
            public override async Task<WorkflowUpdateHandle<TResult>> StartWorkflowUpdateAsync<TResult>(
                StartWorkflowUpdateInput input)
            {
                if (IsPollFrom(input.Args.FirstOrDefault(), owner.failPollFromOffset)
                    && Interlocked.CompareExchange(ref owner.pollFaults, 1, 0) == 0)
                {
                    if (owner.OnPollFault is { } onPollFault)
                    {
                        await onPollFault();
                    }

                    throw Unavailable();
                }

                return await base.StartWorkflowUpdateAsync<TResult>(input);
            }

            public override Task<ActivityHandle<TResult>> StartActivityAsync<TResult>(StartActivityInput input)
            {
                if (input.Activity == ConciergeActivityNames.AuthenticateSession)
                {
                    Interlocked.Increment(ref owner.authenticateStarts);
                    if (owner.PollFaults > 0
                        && Interlocked.Increment(ref owner.reauthenticationAttempts) <= owner.ReauthenticationFailures)
                    {
                        Interlocked.Increment(ref owner.reauthenticationFaults);
                        throw Unavailable();
                    }
                }

                return base.StartActivityAsync<TResult>(input);
            }
        }
    }

    private static async Task PublishEntriesAsync(
        ConciergeTestHost host,
        Guid stayId,
        int count,
        long offsetBase)
    {
        await using var publisher = new WorkflowStreamClient(
            host.Environment.Client,
            ConciergeStayWorkflowIds.ForStay(stayId));
        var topic = publisher.GetTopic<RideActivityEntry>(RideActivityFeed.Topic);
        for (var index = 0; index < count; index++)
        {
            var offset = offsetBase + index;
            topic.Publish(new RideActivityEntry(
                Guid.NewGuid(),
                Sequence: 1,
                Status: "Booked",
                ChangedAtUtc: DateTimeOffset.UtcNow.AddSeconds(offset),
                PickupName: "Hotel",
                DropoffName: "Beach",
                PickupAtUtc: DateTimeOffset.UtcNow.AddHours(1)));
        }

        await publisher.FlushAsync();
    }
}
