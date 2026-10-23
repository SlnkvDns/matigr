using Matigr.Appeals.Application;
using Npgsql;

namespace Matigr.Appeals.Infrastructure;

public sealed class AppealsReadiness(string connectionString) : IAppealsReadiness
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
        }
        catch (Exception e) when (e is NpgsqlException or OperationCanceledException) { }

        return new AppealsReadinessState(database);
    }
}
