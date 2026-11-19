using Matigr.Appeals.Application;
using Npgsql;

namespace Matigr.Appeals.Infrastructure;

public sealed class AppealsReadiness(string connectionString, IReadOnlyCollection<Matigr.Transport.TransportHealth>? transports = null) : IAppealsReadiness
{
    public async Task<AppealsReadinessState> ProbeAsync(CancellationToken cancellationToken)
    {
        var database = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            database = (int?)await command.ExecuteScalarAsync(timeout.Token) == 1;
            await using var states = new NpgsqlCommand("SELECT state,count(*) FROM appeals GROUP BY state", connection);
            await using var reader = await states.ExecuteReaderAsync(timeout.Token);
            var counts = new Dictionary<string, long>();
            while (await reader.ReadAsync(timeout.Token)) counts[reader.GetString(0)] = reader.GetInt64(1);
            AppealsMetrics.Counts(counts);
        }
        catch (Exception e) when (e is NpgsqlException or OperationCanceledException) { }

        return new AppealsReadinessState(database, transports is { Count: > 0 } ? transports.All(x => x.Connected) : null);
    }
}
