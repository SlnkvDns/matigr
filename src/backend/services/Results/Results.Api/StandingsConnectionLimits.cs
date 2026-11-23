using System.Threading.RateLimiting;

namespace Matigr.Results.Api;

internal sealed class StandingsConnectionLimits(IConfiguration configuration) : IDisposable
{
    private readonly ConcurrencyLimiter connections = new(new ConcurrencyLimiterOptions {
        PermitLimit = Positive(configuration, "MaxConnections", 512)
    });
    private readonly TokenBucketRateLimiter attempts = CreateAttemptLimiter(Positive(configuration, "NewConnectionsPerSecond", 32));

    private static int Positive(IConfiguration configuration, string key, int fallback)
    {
        var value = configuration.GetValue($"Snapshots:{key}", fallback);
        return value > 0 ? value : throw new InvalidOperationException($"Snapshots:{key} must be positive.");
    }

    private static TokenBucketRateLimiter CreateAttemptLimiter(int limit) => new(new TokenBucketRateLimiterOptions {
        TokenLimit = limit, TokensPerPeriod = limit, ReplenishmentPeriod = TimeSpan.FromSeconds(1), AutoReplenishment = true
    });

    public RateLimitLease AttemptConnection() => attempts.AttemptAcquire();
    public RateLimitLease AcquireConnection() => connections.AttemptAcquire();

    public void Dispose()
    {
        attempts.Dispose();
        connections.Dispose();
    }
}
