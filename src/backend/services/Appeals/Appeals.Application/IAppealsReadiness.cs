namespace Matigr.Appeals.Application;

public sealed record AppealsReadinessState(bool Database)
{
    public bool Ready => Database;
}

public interface IAppealsReadiness
{
    Task<AppealsReadinessState> ProbeAsync(CancellationToken cancellationToken);
}
