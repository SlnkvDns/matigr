using System.Text.Json.Serialization;
namespace Matigr.Results.Application;

public sealed record StandingTask(string TaskId, string Name);
public sealed record StandingCell(string Status, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Score = null);
public sealed record StandingTeam(string TeamId, string Name, IReadOnlyList<StandingCell> Cells, string Total, int Rank);
public sealed record StandingsSnapshot(string TournamentId, string SnapshotVersion, IReadOnlyList<StandingTask> Tasks, IReadOnlyList<StandingTeam> Teams);
public sealed record MyTaskResult(string TaskId, string ResultId, string ResultVersion, string PublicationId, string Outcome,
    string Score, string AppealAvailabilityHint);
public sealed record MyTournamentResults(string TournamentId, IReadOnlyList<MyTaskResult> Results);
public sealed record PublishedAppealBasis(string ResultId, string ResultVersion, string TournamentId, string TeamId, string TaskId,
    string SolutionId, string MaterialVersionId, string SourceCheckId, string DecisionOrigin, string Outcome, string ScoreRuleVersion,
    string PublicationId, string PublishedAt, string PublicationState = "Published");
public sealed class ResultReadException(int status, string code) : Exception(code)
{ public int Status { get; } = status; public string Code { get; } = code; }
public interface IResultReads
{
    Task<StandingsSnapshot> StandingsAsync(Guid tournament, CancellationToken ct);
    Task<MyTournamentResults> TeamAsync(Guid tournament, Guid team, CancellationToken ct);
    Task<PublishedAppealBasis> BasisAsync(Guid result, long expectedVersion, CancellationToken ct);
}
