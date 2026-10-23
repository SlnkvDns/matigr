namespace Matigr.Results.Application;

public sealed record ResultsReadinessState(bool Database)
{
    public bool Ready => Database;
}

public interface IResultsReadiness
{
    Task<ResultsReadinessState> ProbeAsync(CancellationToken cancellationToken);
}
