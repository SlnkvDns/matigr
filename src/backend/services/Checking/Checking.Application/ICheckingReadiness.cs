namespace Matigr.Checking.Application;

public sealed record DependencyReadiness(bool Database, bool Broker)
{
    public bool Ready => Database && Broker;
}

public interface ICheckingReadiness
{
    Task<DependencyReadiness> ProbeAsync(CancellationToken cancellationToken);
}
