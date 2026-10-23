namespace Matigr.Results.Application;

public sealed record ResultsReadinessState(bool Database, bool? Broker = null)
{
    public bool Ready => Database && Broker != false;
}

public interface IResultsReadiness
{
    Task<ResultsReadinessState> ProbeAsync(CancellationToken cancellationToken);
}
