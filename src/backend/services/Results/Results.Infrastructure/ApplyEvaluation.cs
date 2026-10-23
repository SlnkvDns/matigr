using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matigr.Results.Application;
using Matigr.Results.Domain;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Results.Infrastructure;

internal sealed class ApplyEvaluation(ResultsDbContext db) : IApplyEvaluation
{
    private const string Consumer = "Results.CheckCompleted.v1";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Exact(decimal value) => value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    internal static string Version(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public async Task<EvaluationProcessing> InitialAsync(Evaluation e, Guid messageId, string correlationId, CancellationToken ct = default)
    {
        e.Validate();
        if (e.Kind != EvaluationKind.Initial) throw new ArgumentException("Appeal runtime is RES-BE-06.");
        if (messageId == Guid.Empty || string.IsNullOrWhiteSpace(correlationId)) throw new ArgumentException("Message identity required.");
        var normalized = JsonSerializer.Serialize(e, Json);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Evaluation mutex protects receipt identity even when a contradictory message names another tournament.
        var evaluationId = e.CheckId.ToString("D");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({evaluationId}, 0))", ct);
        var oldInbox = await db.Inbox.AsNoTracking().SingleOrDefaultAsync(x => x.Consumer == Consumer && x.MessageId == messageId, ct);
        if (oldInbox is not null && oldInbox.PayloadHash != hash) throw new InvalidOperationException("Conflicting message identity.");
        var previous = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(x => x.EvaluationId == e.CheckId, ct);
        if (previous is not null)
        {
            if (previous.SemanticHash != hash) throw new InvalidOperationException("Conflicting evaluation identity.");
            if (oldInbox is null) db.Inbox.Add(new() { Consumer = Consumer, MessageId = messageId, PayloadHash = hash, ProcessedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(previous.Status, previous.Response);
        }
        var snapshots = await db.Snapshots.FromSqlInterpolated($"SELECT * FROM tournament_snapshots WHERE tournament_id = {e.Key.TournamentId} FOR UPDATE").ToListAsync(ct);
        var snapshot = snapshots.SingleOrDefault();
        var rule = await db.Rules.AsNoTracking().SingleOrDefaultAsync(x => x.TournamentId == e.Key.TournamentId && x.TaskId == e.Key.TaskId, ct);
        if (snapshot is null || rule is null || !await db.Teams.AnyAsync(x => x.TournamentId == e.Key.TournamentId && x.TeamId == e.Key.TeamId, ct))
        {
            var deferred = await db.Failures.SingleOrDefaultAsync(x => x.Consumer == Consumer && x.MessageId == messageId, ct);
            if (deferred is null) db.Failures.Add(new() { FailureId = Guid.NewGuid(), Consumer = Consumer, MessageId = messageId,
                State = "Deferred", ReasonCode = "ScoringDependencyUnavailable", Envelope = normalized, Attempts = 0 });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new("Deferred", "{}");
        }
        var now = DateTimeOffset.UtcNow;
        var existing = await db.Results.SingleOrDefaultAsync(x => x.TournamentId == e.Key.TournamentId && x.TeamId == e.Key.TeamId && x.TaskId == e.Key.TaskId, ct);
        ResultPublication? publication = null;
        if (existing is null)
        {
            publication = TeamTaskResult.Initial(Guid.NewGuid(), Guid.NewGuid(), e, new(rule.RuleVersion, rule.Points), now).Publication;
            var json = JsonSerializer.Serialize(publication, Json);
            db.Results.Add(new() { ResultId = publication.ResultId, TournamentId = e.Key.TournamentId, TeamId = e.Key.TeamId,
                TaskId = e.Key.TaskId, SolutionId = e.SolutionId, MaterialVersionId = e.MaterialVersionId, ResultVersion = 1,
                Score = publication.Score, Outcome = e.Outcome.ToString(), PublicationId = publication.PublicationId, Publication = json });
            db.Publications.Add(new() { PublicationId = publication.PublicationId, ResultId = publication.ResultId, ResultVersion = 1, Snapshot = json });
            snapshot!.Version = checked(snapshot.Version + 1); snapshot.UpdatedAt = now;
        }
        var status = publication is null ? "Rejected" : "Applied";
        // Initial receipts are local, with no required external consumer. Appeal wire schemas remain separate.
        var response = JsonSerializer.Serialize(new { evaluationId = e.CheckId.ToString("D"), kind = "Initial", status,
            resultId = (publication?.ResultId ?? existing!.ResultId).ToString("D"), expectedResultVersion = "0",
            resultVersion = Version(publication?.ResultVersion ?? existing!.ResultVersion), changed = publication is not null,
            score = publication is null ? null : Exact(publication.Score), publicationId = publication?.PublicationId.ToString("D"),
            publishedAt = publication?.PublishedAt.UtcDateTime.ToString("O"), publicationState = publication is null ? null : "Published",
            sourceCheckId = publication?.Source.CheckId.ToString("D"), correlationId, reasonCode = publication is null ? "InitialResultConflict" : null }, Json);
        db.Receipts.Add(new() { EvaluationId = e.CheckId, SemanticHash = hash, Status = status,
            ResultId = publication?.ResultId ?? existing!.ResultId, Response = response });
        db.Inbox.Add(new() { Consumer = Consumer, MessageId = messageId, PayloadHash = hash, ProcessedAt = now });
        var failure = await db.Failures.SingleOrDefaultAsync(x => x.Consumer == Consumer && x.MessageId == messageId, ct);
        if (failure is not null) failure.State = "Resolved";
        // Primary acknowledgement is the durable receipt above. No duplicate public event is introduced.
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(status, response);
    }
}
