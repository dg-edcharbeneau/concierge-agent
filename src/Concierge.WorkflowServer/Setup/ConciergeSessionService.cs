using Concierge.Contracts;
using Npgsql;

namespace Concierge.WorkflowServer.Setup;

public sealed class ConciergeSessionService(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Creates a session for the guest's eligible stay, bound to the credential digest. Retries with the same
    /// session ID return the original binding. Returns <see langword="null"/> for invalid input or no match.
    /// </summary>
    public async Task<ConciergeSessionContext?> VerifyAsync(
        VerifyConciergeSessionActivityRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.LastName)
            || request.LastName.Trim().Length > 100
            || !RoomNumberFormat.IsValid(request.RoomNumber)
            || request.CredentialSha256 is not { Length: 32 })
        {
            return null;
        }

        // Activity retries and a repeated workflow start use the same SessionId and digest.
        // Return the first committed binding rather than selecting a different stay later.
        await using (var command = dataSource.CreateCommand(
            """
            SELECT cs.stay_id, cs.expires_at_utc, g.given_name, g.family_name, s.room_number
            FROM concierge_sessions cs
            JOIN stays s ON s.stay_id = cs.stay_id
            JOIN guests g ON g.guest_id = s.guest_id
            WHERE cs.session_id = $1 AND cs.credential_sha256 = $2;
            """))
        {
            command.Parameters.AddWithValue(request.SessionId);
            command.Parameters.AddWithValue(request.CredentialSha256);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return new ConciergeSessionContext(
                    request.SessionId,
                    reader.GetGuid(0),
                    reader.GetFieldValue<DateTimeOffset>(1),
                    ReadVerifiedGuest(reader, reader.GetGuid(0), 2));
            }
        }

        Guid stayId;
        DateTimeOffset eligibilityDeadline;
        await using (var command = dataSource.CreateCommand(
            """
            SELECT s.stay_id, s.eligibility_ends_at_utc
            FROM stays s
            JOIN guests g ON g.guest_id = s.guest_id
            WHERE lower(btrim(g.family_name)) = lower(btrim($1))
              AND s.room_number = $2
              AND s.check_in_utc <= CURRENT_TIMESTAMP
              AND s.eligibility_ends_at_utc > CURRENT_TIMESTAMP
            ORDER BY s.stay_id
            LIMIT 2;
            """))
        {
            command.Parameters.AddWithValue(request.LastName.Trim());
            command.Parameters.AddWithValue(request.RoomNumber!);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            stayId = reader.GetGuid(0);
            eligibilityDeadline = reader.GetFieldValue<DateTimeOffset>(1);
            if (await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
        }

        await using (var command = dataSource.CreateCommand(
            """
            INSERT INTO concierge_sessions (
                session_id, stay_id, credential_sha256, expires_at_utc)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (session_id) DO NOTHING;
            """))
        {
            command.Parameters.AddWithValue(request.SessionId);
            command.Parameters.AddWithValue(stayId);
            command.Parameters.AddWithValue(request.CredentialSha256);
            command.Parameters.AddWithValue(eligibilityDeadline);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // A concurrent retry may have inserted this ID. Only accept the matching verifier.
        await using (var command = dataSource.CreateCommand(
            """
            SELECT cs.stay_id, cs.expires_at_utc, g.given_name, g.family_name, s.room_number
            FROM concierge_sessions cs
            JOIN stays s ON s.stay_id = cs.stay_id
            JOIN guests g ON g.guest_id = s.guest_id
            WHERE cs.session_id = $1 AND cs.credential_sha256 = $2;
            """))
        {
            command.Parameters.AddWithValue(request.SessionId);
            command.Parameters.AddWithValue(request.CredentialSha256);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new ConciergeSessionContext(
                    request.SessionId,
                    reader.GetGuid(0),
                    reader.GetFieldValue<DateTimeOffset>(1),
                    ReadVerifiedGuest(reader, reader.GetGuid(0), 2))
                : null;
        }
    }

    /// <summary>
    /// Returns the active session for a credential digest when neither the session nor the stay has expired;
    /// otherwise <see langword="null"/>.
    /// </summary>
    public async Task<ConciergeSessionContext?> AuthenticateAsync(
        byte[]? credentialSha256,
        CancellationToken cancellationToken)
    {
        if (credentialSha256 is not { Length: 32 })
        {
            return null;
        }

        await using var command = dataSource.CreateCommand(
            """
            SELECT cs.session_id, cs.stay_id, cs.expires_at_utc,
                   g.given_name, g.family_name, s.room_number
            FROM concierge_sessions cs
            JOIN stays s ON s.stay_id = cs.stay_id
            JOIN guests g ON g.guest_id = s.guest_id
            WHERE cs.credential_sha256 = $1
              AND cs.ended_at_utc IS NULL
              AND cs.expires_at_utc > CURRENT_TIMESTAMP
              AND s.check_in_utc <= CURRENT_TIMESTAMP
              AND s.eligibility_ends_at_utc > CURRENT_TIMESTAMP;
            """);
        command.Parameters.AddWithValue(credentialSha256);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ConciergeSessionContext(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            ReadVerifiedGuest(reader, reader.GetGuid(1), 3));
    }

    private static ConciergeVerifiedGuest ReadVerifiedGuest(NpgsqlDataReader reader, Guid stayId, int offset) =>
        new(stayId, reader.GetString(offset), reader.GetString(offset + 1), reader.GetString(offset + 2));

    /// <summary>
    /// Marks the session ended. Already-ended sessions are left unchanged, so retries are safe.
    /// </summary>
    public async Task InvalidateAsync(
        InvalidateConciergeSessionActivityRequest session,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE concierge_sessions
            SET ended_at_utc = CURRENT_TIMESTAMP
            WHERE session_id = $1
              AND stay_id = $2
              AND ended_at_utc IS NULL;
            """);
        command.Parameters.AddWithValue(session.SessionId);
        command.Parameters.AddWithValue(session.StayId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
