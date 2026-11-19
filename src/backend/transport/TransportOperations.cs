using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Matigr.Transport;

public static class TransportOperations
{
    // Operator-only CLI, no business/HTTP redrive endpoint. Source payload/IDs remain immutable.
    public static async Task<bool> RunAsync(IServiceProvider services, IConfiguration configuration, CancellationToken ct = default)
    {
        if (configuration["Transport:Redrive"] is not { } value) return false;
        if (!Guid.TryParse(value, out var failure)) throw new ArgumentException("A quarantine record ID is required.");
        var applied = await services.GetRequiredService<PostgresTransportStore>().RedriveAsync(failure, ct);
        Console.WriteLine(applied ? "Transport redrive recorded." : "Quarantine record unavailable.");
        Environment.ExitCode = applied ? 0 : 1; return true;
    }
}
