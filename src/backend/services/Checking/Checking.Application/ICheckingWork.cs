using System.Text.Json;
using System.Text.Json.Serialization;
using Matigr.Checking.Application.Identity;

namespace Matigr.Checking.Application;

// Published HTTP views, separate from domain/persistence records. Versions stay exact strings.
public sealed record CheckView(string CheckId, string SolutionId, string MaterialVersionId, string TournamentId,
    string TeamId, string TaskId, string Kind, string State, string Version, string ReviewRevision, string CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Outcome = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DecisionOrigin = null);
public sealed record AssignmentView(CheckView Check, string AssignmentId, string ReviewerId, string StartedAt, string Capability)
{
    public override string ToString() => nameof(AssignmentView);
}
public sealed record QueueItem(string CheckId, string SolutionId, string TournamentId, string TeamId,
    string TaskId, string Kind, string State, string CreatedAt);
public sealed record QueuePage(string TournamentId, string Queue, IReadOnlyList<QueueItem> Items, string? NextCursor);
public sealed record TakeNextReply(AssignmentView? Assignment, string? ErrorCode, string CorrelationId)
{
    public int Status => Assignment is not null ? 200 : ErrorCode is not null ? 409 : 204;
    public string? ETag => Assignment is null ? null : $"\"{Assignment.Check.Version}\"";
    public override string ToString() => nameof(TakeNextReply);
}
public sealed record QueueReadOptions(int PageSize = 50);
public sealed class CheckingOperationException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public static class CheckingJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

public interface ICheckingWork
{
    Task<QueuePage> QueueAsync(AuthenticatedSubject subject, Guid tournamentId, string queue, string? cursor, CancellationToken cancellationToken);
    Task<TakeNextReply> TakeNextAsync(AuthenticatedSubject subject, Guid tournamentId, string queue, string key, string correlationId, CancellationToken cancellationToken);
    Task<AssignmentView?> MyAssignmentAsync(AuthenticatedSubject subject, CancellationToken cancellationToken);
    Task<CheckView> CheckAsync(AuthenticatedSubject subject, Guid checkId, CancellationToken cancellationToken);
    Task<Stream> MaterialAsync(AuthenticatedSubject subject, Guid checkId, CancellationToken cancellationToken);
}
