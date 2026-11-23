using Matigr.Checking.Domain;

namespace Matigr.Checking.Application;

public interface IReadOnlyMaterialStore
{
    Task<Stream> OpenAsync(SolutionReference solution, CancellationToken cancellationToken);
}
