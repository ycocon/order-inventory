using System.Data;
using Npgsql;

namespace PaymentSimulator;

internal sealed record Authorization(Guid OperationId, long AmountMinor, string Currency,
    string Decision, DateTime DecidedAt);

internal sealed class AuthorizationLedger(NpgsqlDataSource dataSource)
{
    public async Task<(Authorization Authorization, bool Created)> AuthorizeAsync(
        Guid id, long amount, string currency, string decision, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO payment_authorizations (operation_id, amount_minor, currency, decision)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (operation_id) DO NOTHING
            RETURNING operation_id, amount_minor, currency, decision, decided_at
            """, connection, transaction);
        insert.Parameters.Add(new NpgsqlParameter { Value = id });
        insert.Parameters.Add(new NpgsqlParameter { Value = amount });
        insert.Parameters.Add(new NpgsqlParameter { Value = currency });
        insert.Parameters.Add(new NpgsqlParameter { Value = decision });
        var inserted = await ReadAsync(insert, ct);
        // A separate statement at READ COMMITTED sees the decision of the winning insert.
        await using var lookup = new NpgsqlCommand(LookupSql, connection, transaction);
        lookup.Parameters.Add(new NpgsqlParameter { Value = id });
        var authorization = inserted ?? await ReadAsync(lookup, ct)
            ?? throw new InvalidOperationException("The prior authorization was missing.");
        await transaction.CommitAsync(ct);
        return (authorization, inserted is not null);
    }

    public async Task<Authorization?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var lookup = dataSource.CreateCommand(LookupSql);
        lookup.Parameters.Add(new NpgsqlParameter { Value = id });
        return await ReadAsync(lookup, ct);
    }

    public async Task CheckReadyAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT operation_id FROM payment_authorizations LIMIT 0");
        await command.ExecuteNonQueryAsync(ct);
    }

    private const string LookupSql = """
        SELECT operation_id, amount_minor, currency, decision, decided_at
        FROM payment_authorizations
        WHERE operation_id = $1
        """;

    private static async Task<Authorization?> ReadAsync(NpgsqlCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new Authorization(reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.GetDateTime(4))
            : null;
    }
}
