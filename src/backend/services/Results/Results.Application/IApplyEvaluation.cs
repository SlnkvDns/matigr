using Matigr.Results.Domain;
namespace Matigr.Results.Application;

public sealed record EvaluationProcessing(string Status, string Response);
public interface IApplyEvaluation
{
    Task<EvaluationProcessing> InitialAsync(Evaluation evaluation, Guid messageId, string correlationId, CancellationToken ct = default);
}
