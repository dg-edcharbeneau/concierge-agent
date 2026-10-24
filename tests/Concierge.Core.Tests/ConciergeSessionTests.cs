using System.Security.Cryptography;
using Concierge.Contracts;
using Concierge.WorkflowServer.Data;
using Concierge.WorkflowServer.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Concierge.Core.Tests;

public sealed class ConciergeSessionTests(PostgreSqlFixture postgreSql)
    : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    [Fact]
    public async Task VerificationRequiresMatchingNameAndRoomAndIssuesFreshHashedCredentials()
    {
        var dataSource = await CreateDatabaseAsync();
        var (firstStay, secondStay) = await SeedTwoEligibleStaysAsync(dataSource);
        var sessions = new ConciergeSessionService(dataSource, ConciergeTestHost.HotelOptions);

        var invalidId = Guid.NewGuid();
        var invalidDigest = RandomNumberGenerator.GetBytes(32);
        Assert.Null(await sessions.VerifyAsync(
            new VerifyConciergeSessionActivityRequest(
                invalidId, invalidDigest, "Wrong", "412"),
            CancellationToken.None));
        Assert.Null(await sessions.VerifyAsync(
            new VerifyConciergeSessionActivityRequest(
                Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), "Rivera", "0412"),
            CancellationToken.None));
        Assert.Null(await sessions.VerifyAsync(
            new VerifyConciergeSessionActivityRequest(
                Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), "Rivera", "12345"),
            CancellationToken.None));

        var firstDigest = RandomNumberGenerator.GetBytes(32);
        var firstRequest = new VerifyConciergeSessionActivityRequest(
            Guid.NewGuid(), firstDigest, " rIvErA ", "412");
        var first = Assert.IsType<ConciergeSessionContext>(await sessions.VerifyAsync(
            firstRequest,
            CancellationToken.None));
        Assert.Equal(first, await sessions.VerifyAsync(firstRequest, CancellationToken.None));
        var secondDigest = RandomNumberGenerator.GetBytes(32);
        var second = Assert.IsType<ConciergeSessionContext>(await sessions.VerifyAsync(
            new VerifyConciergeSessionActivityRequest(
                Guid.NewGuid(), secondDigest, "Rivera", "412"),
            CancellationToken.None));

        Assert.Equal(firstStay, first.StayId);
        Assert.NotEqual(secondStay, first.StayId);
        Assert.Equal(
            new ConciergeVerifiedGuest(
                firstStay,
                "Jordan",
                "Rivera",
                "412",
                Guid.Parse("bca99f5d-8e64-5f1b-bc77-a12d9385b104"),
                ConciergeTestHost.HotelToday.AddDays(1),
                ConciergeTestHost.HotelToday.AddDays(-1)),
            first.VerifiedGuest);
        var otherDigest = RandomNumberGenerator.GetBytes(32);
        var other = await sessions.VerifyAsync(
            new VerifyConciergeSessionActivityRequest(Guid.NewGuid(), otherDigest, "Morgan", "812"),
            CancellationToken.None);
        Assert.Equal(
            new ConciergeVerifiedGuest(
                secondStay,
                "Taylor",
                "Morgan",
                "812",
                Guid.Parse("e9f93b63-8607-58f7-8c5a-296bcbe7ea38"),
                ConciergeTestHost.HotelToday.AddDays(1),
                ConciergeTestHost.HotelToday.AddDays(-1)),
            other!.VerifiedGuest);
        Assert.Equal(first.VerifiedGuest, (await sessions.AuthenticateAsync(firstDigest, CancellationToken.None))!.VerifiedGuest);
        Assert.Equal(
            firstDigest,
            await ReadCredentialHashAsync(dataSource, first.SessionId));
        Assert.Equal(first, await sessions.AuthenticateAsync(firstDigest, CancellationToken.None));
        Assert.Null(await sessions.AuthenticateAsync(SHA256.HashData("forged-token"u8.ToArray()), CancellationToken.None));

        await sessions.InvalidateAsync(
            new InvalidateConciergeSessionActivityRequest(first.SessionId, first.StayId),
            CancellationToken.None);
        Assert.Null(await sessions.AuthenticateAsync(firstDigest, CancellationToken.None));

        await SetSessionExpiryAsync(dataSource, second.SessionId);
        Assert.Null(await sessions.AuthenticateAsync(secondDigest, CancellationToken.None));
    }

    [Fact]
    public async Task VerificationRejectsLeadingZeroRoomNumbersEvenWhenAStoredRowMatches()
    {
        var dataSource = await CreateDatabaseAsync();
        await using (var command = dataSource.CreateCommand(
            """
            INSERT INTO guests (guest_id, given_name, family_name)
            VALUES ('bca99f5d-8e64-5f1b-bc77-a12d9385b104', 'Jordan', 'Rivera');
            """))
        {
            await command.ExecuteNonQueryAsync();
        }

        // The schema still admits legacy zero-padded rows; lookup must not reach them.
        await InsertStayAsync(
            dataSource,
            Guid.NewGuid(),
            Guid.Parse("bca99f5d-8e64-5f1b-bc77-a12d9385b104"),
            "0412");

        Assert.Null(await new ConciergeSessionService(dataSource, ConciergeTestHost.HotelOptions).VerifyAsync(
            new VerifyConciergeSessionActivityRequest(
                Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), "Rivera", "0412"),
            CancellationToken.None));
    }

    [Fact]
    public async Task AmbiguousCorruptStayMatchesFailClosed()
    {
        var dataSource = await CreateDatabaseAsync();
        await SeedTwoEligibleStaysAsync(dataSource);
        await using (var dropConstraint = dataSource.CreateCommand(
            "ALTER TABLE stays DROP CONSTRAINT ex_stays_room_eligibility_overlap;"))
        {
            await dropConstraint.ExecuteNonQueryAsync();
        }

        await InsertStayAsync(
            dataSource,
            Guid.NewGuid(),
            Guid.Parse("bca99f5d-8e64-5f1b-bc77-a12d9385b104"),
            "412");

        var match = await new ConciergeSessionService(dataSource, ConciergeTestHost.HotelOptions).VerifyAsync(
            new VerifyConciergeSessionActivityRequest(
                Guid.NewGuid(),
                RandomNumberGenerator.GetBytes(32),
                "Rivera",
                "412"),
            CancellationToken.None);

        Assert.Null(match);
    }

    [Fact]
    public async Task StaysOutsideTheirLocalEligibilityDaysCannotVerifyOrAuthenticate()
    {
        var dataSource = await CreateDatabaseAsync();
        await using (var command = dataSource.CreateCommand(
            """
            INSERT INTO guests (guest_id, given_name, family_name)
            VALUES ('bca99f5d-8e64-5f1b-bc77-a12d9385b104', 'Jordan', 'Rivera');
            INSERT INTO stays (
                stay_id, external_stay_key, guest_id, room_number,
                check_in_utc, check_out_utc, eligibility_ends_at_utc)
            SELECT gen_random_uuid(), key, 'bca99f5d-8e64-5f1b-bc77-a12d9385b104', room,
                   ((today + check_in_offset)::timestamp AT TIME ZONE 'America/New_York'),
                   ((today + check_out_offset)::timestamp AT TIME ZONE 'America/New_York'),
                   ((today + check_out_offset + 1)::timestamp AT TIME ZONE 'America/New_York')
            FROM (SELECT (CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date AS today) d,
                 (VALUES ('checked-out', '101', -3, -1),
                         ('future', '202', 1, 3),
                         ('checkout-today', '303', -2, 0)) AS s(key, room, check_in_offset, check_out_offset);
            """))
        {
            await command.ExecuteNonQueryAsync();
        }

        var sessions = new ConciergeSessionService(dataSource, ConciergeTestHost.HotelOptions);
        Assert.Null(await sessions.VerifyAsync(
            new VerifyConciergeSessionActivityRequest(
                Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), "Rivera", "101"),
            CancellationToken.None));
        Assert.Null(await sessions.VerifyAsync(
            new VerifyConciergeSessionActivityRequest(
                Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), "Rivera", "202"),
            CancellationToken.None));

        // Checkout day remains eligible until the following local midnight, then not at all.
        var checkoutDigest = RandomNumberGenerator.GetBytes(32);
        var checkoutDay = Assert.IsType<ConciergeSessionContext>(await sessions.VerifyAsync(
            new VerifyConciergeSessionActivityRequest(
                Guid.NewGuid(), checkoutDigest, "Rivera", "303"),
            CancellationToken.None));
        await using (var expire = dataSource.CreateCommand(
            """
            UPDATE stays
            SET check_in_utc = ((d.today - 3)::timestamp AT TIME ZONE 'America/New_York'),
                check_out_utc = ((d.today - 1)::timestamp AT TIME ZONE 'America/New_York'),
                eligibility_ends_at_utc = (d.today::timestamp AT TIME ZONE 'America/New_York')
            FROM (SELECT (CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date AS today) d
            WHERE stay_id = $1;
            """))
        {
            expire.Parameters.AddWithValue(checkoutDay.StayId);
            await expire.ExecuteNonQueryAsync();
        }

        Assert.Null(await sessions.AuthenticateAsync(checkoutDigest, CancellationToken.None));
    }

    [Fact]
    public async Task RoomCannotHaveOverlappingEligibleOccupants()
    {
        var dataSource = await CreateDatabaseAsync();
        await SeedTwoEligibleStaysAsync(dataSource);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertStayAsync(
                dataSource,
                Guid.NewGuid(),
                Guid.Parse("e9f93b63-8607-58f7-8c5a-296bcbe7ea38"),
                "412"));

        Assert.Equal(PostgresErrorCodes.ExclusionViolation, error.SqlState);
    }

    private async Task<NpgsqlDataSource> CreateDatabaseAsync()
    {
        var database = await postgreSql.CreateDatabaseAsync();
        var source = NpgsqlDataSource.Create(database.ConnectionString);
        // Keep the database alive with the data source. Disposal drops both.
        databaseOwners.Add((database, source));
        await new DatabaseMigrator(source, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();
        return source;
    }

    private readonly List<(PostgreSqlTestDatabase Database, NpgsqlDataSource DataSource)> databaseOwners = [];

    public Task InitializeAsync() => Task.CompletedTask;

    private async Task<(Guid First, Guid Second)> SeedTwoEligibleStaysAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO guests (guest_id, given_name, family_name)
            VALUES
                ('bca99f5d-8e64-5f1b-bc77-a12d9385b104', 'Jordan', 'Rivera'),
                ('e9f93b63-8607-58f7-8c5a-296bcbe7ea38', 'Taylor', 'Morgan');
            """);
        await command.ExecuteNonQueryAsync();
        var firstStay = Guid.NewGuid();
        var secondStay = Guid.NewGuid();
        await InsertStayAsync(
            dataSource,
            firstStay,
            Guid.Parse("bca99f5d-8e64-5f1b-bc77-a12d9385b104"),
            "412");
        await InsertStayAsync(
            dataSource,
            secondStay,
            Guid.Parse("e9f93b63-8607-58f7-8c5a-296bcbe7ea38"),
            "812");
        return (firstStay, secondStay);
    }

    private static async Task InsertStayAsync(
        NpgsqlDataSource dataSource,
        Guid stayId,
        Guid guestId,
        string roomNumber)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO stays (
                stay_id, external_stay_key, guest_id, room_number,
                check_in_utc, check_out_utc, eligibility_ends_at_utc)
            VALUES (
                $1, $2, $3, $4,
                (((CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date - 1)::timestamp
                    AT TIME ZONE 'America/New_York'),
                (((CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date + 1)::timestamp
                    AT TIME ZONE 'America/New_York'),
                (((CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date + 2)::timestamp
                    AT TIME ZONE 'America/New_York'));
            """);
        command.Parameters.AddWithValue(stayId);
        command.Parameters.AddWithValue($"stay-{stayId:N}");
        command.Parameters.AddWithValue(guestId);
        command.Parameters.AddWithValue(roomNumber);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<byte[]> ReadCredentialHashAsync(
        NpgsqlDataSource dataSource,
        Guid sessionId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT credential_sha256 FROM concierge_sessions WHERE session_id = $1;");
        command.Parameters.AddWithValue(sessionId);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private static async Task SetSessionExpiryAsync(
        NpgsqlDataSource dataSource,
        Guid sessionId)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE concierge_sessions
            SET expires_at_utc = CURRENT_TIMESTAMP - INTERVAL '1 minute'
            WHERE session_id = $1;
            """);
        command.Parameters.AddWithValue(sessionId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var (database, dataSource) in databaseOwners)
        {
            await dataSource.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
