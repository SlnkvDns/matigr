namespace Matigr.Results.Application;

public interface ISnapshotVersions
{
    Task<string> CurrentAsync(Guid tournament, CancellationToken ct);
}
