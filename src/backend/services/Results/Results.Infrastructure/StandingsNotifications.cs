using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Matigr.Results.Infrastructure;

internal sealed class StandingsNotifications(IConfiguration configuration, StandingsCache cache, ILogger<StandingsNotifications> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("Results");
        if (string.IsNullOrWhiteSpace(connectionString) || configuration.GetValue<bool?>("Snapshots:Listen") == false) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Notifications belong to the listening session. Never return that session to the query pool.
                await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
                var pending = new Dictionary<Guid, long>();
                connection.Notification += (_, args) => {
                    var fields = args.Payload.AsSpan();
                    var separator = fields.IndexOf(':');
                    if (separator > 0 && Guid.TryParse(fields[..separator], out var tournament)
                        && long.TryParse(fields[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
                        pending[tournament] = Math.Max(pending.GetValueOrDefault(tournament), version);
                };
                await connection.OpenAsync(stoppingToken);
                await using var command = new NpgsqlCommand("LISTEN matigr_standings", connection);
                await command.ExecuteNonQueryAsync(stoppingToken);
                await cache.ReconcileAsync(stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    foreach (var (tournament, version) in pending)
                        await cache.RefreshSubscribersAsync(tournament, version, stoppingToken);
                    pending.Clear();
                    if (!await connection.WaitAsync(30_000, stoppingToken)) await cache.ReconcileAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) when (e is NpgsqlException or TimeoutException or System.IO.IOException)
            {
                logger.LogWarning("Results snapshot listener disconnected. Reconnecting.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
