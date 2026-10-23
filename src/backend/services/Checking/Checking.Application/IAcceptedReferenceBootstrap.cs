using Matigr.Checking.Domain;

namespace Matigr.Checking.Application;

// Internal prepared-data boundary; not an intake API or a SolutionAccepted consumer.
public interface IAcceptedReferenceBootstrap
{
    Task<BootstrappedCheck> EnsureAsync(SolutionReference snapshot, CancellationToken cancellationToken);
}

public sealed record BootstrappedCheck(Guid CheckId, bool Created);
public sealed class AcceptedReferenceConflictException() : Exception("The accepted solution conflicts with stored immutable data.");
public sealed class MaterialNotAllowedException() : Exception("The solution is not in the prepared material allowlist.");
public sealed class MaterialUnavailableException(Exception? inner = null) : Exception("Prepared material is unavailable or failed integrity verification.", inner);
