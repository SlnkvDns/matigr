using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Matigr.Results.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Matigr.Results.Infrastructure;

public sealed record CachedStandings(long Version, byte[] Json);

public sealed class StandingsCache(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, Entry> entries = new();

    private sealed class Entry
    {
        internal readonly SemaphoreSlim Build = new(1);
        internal readonly HashSet<ChannelWriter<CachedStandings>> Subscribers = [];
        internal CachedStandings? Current;
    }

    public async Task<CachedStandings> ReadAsync(Guid tournament, CancellationToken ct)
    {
        var version = await ReadVersionAsync(tournament, ct);
        return await RefreshAsync(tournament, entries.GetOrAdd(tournament, _ => new()), version, ct);
    }

    public async Task<Subscription> SubscribeAsync(Guid tournament, CancellationToken ct)
    {
        await ReadVersionAsync(tournament, ct);
        var entry = entries.GetOrAdd(tournament, _ => new());
        var channel = Channel.CreateBounded<CachedStandings>(new BoundedChannelOptions(1) {
            SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        var subscription = new Subscription(channel.Reader, () => {
            lock (entry.Subscribers) entry.Subscribers.Remove(channel.Writer);
            channel.Writer.TryComplete();
        });
        lock (entry.Subscribers) entry.Subscribers.Add(channel.Writer);
        try
        {
            // Repair a commit between validating the tournament and registering the subscription.
            var version = await ReadVersionAsync(tournament, ct);
            await RefreshAsync(tournament, entry, version, ct);
            lock (entry.Subscribers) channel.Writer.TryWrite(entry.Current!);
            return subscription;
        }
        catch
        {
            subscription.Dispose();
            throw;
        }
    }

    internal async Task RefreshSubscribersAsync(Guid tournament, long version, CancellationToken ct)
    {
        if (!entries.TryGetValue(tournament, out var entry) || !HasSubscribers(entry)) return;
        await RefreshAsync(tournament, entry, version, ct);
    }

    internal async Task ReconcileAsync(CancellationToken ct)
    {
        foreach (var (tournament, entry) in entries)
        {
            if (!HasSubscribers(entry)) continue;
            var version = await ReadVersionAsync(tournament, ct);
            await RefreshAsync(tournament, entry, version, ct);
        }
    }

    private static bool HasSubscribers(Entry entry)
    {
        lock (entry.Subscribers) return entry.Subscribers.Count != 0;
    }

    private async Task<long> ReadVersionAsync(Guid tournament, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var version = await scope.ServiceProvider.GetRequiredService<ISnapshotVersions>().CurrentAsync(tournament, ct);
        return ParseVersion(version);
    }

    private async Task<CachedStandings> RefreshAsync(Guid tournament, Entry entry, long version, CancellationToken ct)
    {
        if (Volatile.Read(ref entry.Current) is { } cached && cached.Version >= version) return cached;
        await entry.Build.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref entry.Current) is { } current && current.Version >= version) return current;
            await using var scope = scopes.CreateAsyncScope();
            // The shared rebuild survives cancellation of the particular viewer who started it.
            var snapshot = await scope.ServiceProvider.GetRequiredService<IResultReads>()
                .StandingsAsync(tournament, lifetime.ApplicationStopping);
            var published = new CachedStandings(ParseVersion(snapshot.SnapshotVersion), JsonSerializer.SerializeToUtf8Bytes(snapshot, Json));
            lock (entry.Subscribers)
            {
                Volatile.Write(ref entry.Current, published);
                foreach (var subscriber in entry.Subscribers) subscriber.TryWrite(published);
            }
            return published;
        }
        finally { entry.Build.Release(); }
    }

    private static long ParseVersion(string version) => long.Parse(version, NumberStyles.None, CultureInfo.InvariantCulture);

    public sealed class Subscription(ChannelReader<CachedStandings> snapshots, Action release) : IDisposable
    {
        public ChannelReader<CachedStandings> Snapshots { get; } = snapshots;
        public void Dispose() => release();
    }
}
