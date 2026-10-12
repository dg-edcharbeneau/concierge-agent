using Concierge.WorkflowServer.Workflows;
using Concierge.Contracts;
using Npgsql;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Concierge.WorkflowServer.Activities;

/// <summary>Retry-safe transcript I/O keyed by stay ID and stable turn ID.</summary>
public sealed class ConciergeTranscriptActivities(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Inserts transcript turns in one transaction. Turns already stored are skipped, but a stored turn
    /// whose content differs fails the activity, so retries can never rewrite history.
    /// </summary>
    [Activity(nameof(PersistAsync))]
    public async Task PersistAsync(PersistTranscriptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var turn in request.Turns)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO transcript_turns (
                    transcript_turn_id,
                    stay_id,
                    turn_id,
                    turn_sequence,
                    channel,
                    guest_message,
                    assistant_message,
                    occurred_at_utc,
                    interrupted)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                ON CONFLICT (stay_id, turn_id) DO NOTHING;
                """,
                connection,
                transaction);
            insert.Parameters.AddWithValue(Guid.NewGuid());
            insert.Parameters.AddWithValue(request.StayId);
            insert.Parameters.AddWithValue(turn.TurnId);
            insert.Parameters.AddWithValue(turn.Sequence);
            insert.Parameters.AddWithValue(turn.Channel);
            insert.Parameters.AddWithValue(turn.GuestMessage);
            insert.Parameters.AddWithValue(turn.AssistantMessage);
            insert.Parameters.AddWithValue(turn.OccurredAtUtc);
            insert.Parameters.AddWithValue(turn.Interrupted);

            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await EnsureExistingTurnMatchesAsync(
                    connection,
                    transaction,
                    request.StayId,
                    turn,
                    cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Returns every persisted turn for the stay in sequence order.
    /// </summary>
    [Activity(nameof(ReadAsync))]
    public async Task<IReadOnlyList<ConciergeTranscriptTurn>> ReadAsync(ReadTranscriptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        await using var command = dataSource.CreateCommand(
            """
            SELECT turn_id, turn_sequence, channel, guest_message,
                   assistant_message, occurred_at_utc, interrupted
            FROM transcript_turns
            WHERE stay_id = $1
            ORDER BY turn_sequence, turn_id;
            """);
        command.Parameters.AddWithValue(request.StayId);
        var turns = new List<ConciergeTranscriptTurn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            turns.Add(ReadTurn(reader));
        }

        return turns;
    }

    /// <summary>
    /// Returns the persisted turn with the given turn ID, or <see langword="null"/> if it was never stored.
    /// Used after Continue-As-New to detect a retried turn from an earlier run.
    /// </summary>
    [Activity(nameof(FindAsync))]
    public async Task<ConciergeTranscriptTurn?> FindAsync(FindTranscriptTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        await using var command = dataSource.CreateCommand(
            """
            SELECT turn_id, turn_sequence, channel, guest_message,
                   assistant_message, occurred_at_utc, interrupted
            FROM transcript_turns
            WHERE stay_id = $1 AND turn_id = $2;
            """);
        command.Parameters.AddWithValue(request.StayId);
        command.Parameters.AddWithValue(request.TurnId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTurn(reader) : null;
    }

    /// <summary>
    /// Marks a persisted voice turn as interrupted. This monotonic update is safe to retry and
    /// intentionally does nothing when the turn has not been persisted yet.
    /// </summary>
    [Activity(nameof(MarkInterruptedAsync))]
    public async Task MarkInterruptedAsync(MarkTranscriptTurnInterruptedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        await using var command = dataSource.CreateCommand(
            """
            UPDATE transcript_turns
            SET interrupted = TRUE
            WHERE stay_id = $1 AND turn_id = $2 AND channel = 'voice';
            """);
        command.Parameters.AddWithValue(request.StayId);
        command.Parameters.AddWithValue(request.TurnId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ConciergeTranscriptTurn ReadTurn(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetBoolean(6));

    private static async Task EnsureExistingTurnMatchesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid stayId,
        ConciergeTranscriptTurn turn,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT turn_sequence, channel, guest_message, assistant_message
            FROM transcript_turns
            WHERE stay_id = $1 AND turn_id = $2;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(stayId);
        command.Parameters.AddWithValue(turn.TurnId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || reader.GetInt64(0) != turn.Sequence
            || !StringComparer.Ordinal.Equals(reader.GetString(1), turn.Channel)
            || !StringComparer.Ordinal.Equals(reader.GetString(2), turn.GuestMessage)
            || !StringComparer.Ordinal.Equals(reader.GetString(3), turn.AssistantMessage))
        {
            // Retrying cannot resolve a conflicting stored row.
            throw new ApplicationFailureException(
                "A persisted transcript turn conflicts with its stable turn identifier.",
                errorType: "ConciergeTranscriptConflict",
                nonRetryable: true);
        }
    }
}
