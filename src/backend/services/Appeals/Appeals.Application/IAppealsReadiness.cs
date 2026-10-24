namespace Matigr.Appeals.Application;

public sealed record AppealsReadinessState(bool Database, bool? Broker = null)
{
    public bool Ready => Database && Broker != false;
}

public interface IAppealsReadiness
{
    Task<AppealsReadinessState> ProbeAsync(CancellationToken cancellationToken);
}
