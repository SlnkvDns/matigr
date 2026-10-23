using Matigr.Results.Application;
using Npgsql;

namespace Matigr.Results.Infrastructure;

public sealed class ResultsReadiness(string connectionString) : IResultsReadiness
{
    public async Task<ResultsReadinessState> ProbeAsync(CancellationToken cancellationToken)
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
        }
        catch (Exception e) when (e is NpgsqlException or OperationCanceledException) { }

        return new ResultsReadinessState(database);
    }
}
