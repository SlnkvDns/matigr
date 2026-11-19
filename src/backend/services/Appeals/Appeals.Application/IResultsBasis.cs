using Matigr.Appeals.Domain;
namespace Matigr.Appeals.Application;
public sealed class AppealOperationException(int status, string code) : Exception(code)
{ public int Status { get; } = status; public string Code { get; } = code; }
public interface IResultsBasis
{
    Task<PublishedBasis> GetAsync(Guid result, long expectedVersion, CancellationToken cancellationToken);
}
