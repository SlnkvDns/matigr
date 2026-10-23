using Matigr.Checking.Application;
using Npgsql;
using RabbitMQ.Client;

namespace Matigr.Checking.Infrastructure;

public sealed class CheckingReadiness(string connectionString, ConnectionFactory broker, Matigr.Transport.TransportHealth? runtime = null) : ICheckingReadiness
{
    public async Task<DependencyReadiness> ProbeAsync(CancellationToken cancellationToken)
    {
        var database = false;
        var transport = false;
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

        try
        {
            await using var connection = await broker.CreateConnectionAsync(cancellationToken: timeout.Token);
            transport = connection.IsOpen;
        }
        catch (Exception e) when (e is RabbitMQ.Client.Exceptions.BrokerUnreachableException or OperationCanceledException) { }
        return new DependencyReadiness(database, transport && (runtime?.Connected ?? true));
    }
}
