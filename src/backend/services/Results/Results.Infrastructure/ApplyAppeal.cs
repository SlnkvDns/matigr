using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matigr.Results.Application;
using Matigr.Results.Domain;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Results.Infrastructure;

internal sealed partial class ApplyEvaluation
{
    public async Task<EvaluationProcessing> AppealAsync(Evaluation e, Guid messageId, string correlationId, CancellationToken ct = default)
    {
        e.Validate();
        if (e.Kind != EvaluationKind.Appeal || messageId == Guid.Empty || string.IsNullOrWhiteSpace(correlationId)) throw new ArgumentException("Appeal identity required.");
        var a = e.Appeal!;
        var normalized = JsonSerializer.Serialize(e, Json);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var evaluationId = e.CheckId.ToString("D");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({evaluationId}, 0))", ct);
        var inbox = await db.Inbox.AsNoTracking().SingleOrDefaultAsync(x => x.Consumer == Consumer && x.MessageId == messageId, ct);
        if (inbox is not null && inbox.PayloadHash != hash) throw new EvaluationIdentityConflictException();
        var previous = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(x => x.EvaluationId == e.CheckId, ct);
        if (previous is not null) {
            if (previous.SemanticHash != hash) throw new EvaluationIdentityConflictException();
            if (inbox is null) db.Inbox.Add(new() { Consumer = Consumer, MessageId = messageId, PayloadHash = hash, ProcessedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(previous.Status, previous.Response);
        }
        var snapshot = (await db.Snapshots.FromSqlInterpolated($"SELECT * FROM tournament_snapshots WHERE tournament_id={e.Key.TournamentId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        var row = await db.Results.SingleOrDefaultAsync(x => x.TournamentId == e.Key.TournamentId && x.TeamId == e.Key.TeamId && x.TaskId == e.Key.TaskId, ct);
        if (snapshot is null || row is null) return new("Deferred", "{}");
        var before = JsonSerializer.Deserialize<ResultPublication>(row.Publication, Json) ?? throw new InvalidOperationException("Invalid stored publication.");
        var result = new TeamTaskResult(before);
        var now = DateTimeOffset.UtcNow;
        var receipt = result.Apply(e, Guid.NewGuid(), now);
        var status = receipt.Applied ? "Applied" : "Rejected";
        var payload = new Dictionary<string, object?> {
            ["evaluationId"] = e.CheckId.ToString("D"), ["kind"] = "Appeal", ["reviewerId"] = e.ReviewerId.ToString("D"),
            ["assignmentId"] = e.AssignmentId.ToString("D"), ["evaluationDecisionOrigin"] = e.Origin.ToString(),
            ["solutionId"] = e.SolutionId.ToString("D"), ["tournamentId"] = e.Key.TournamentId.ToString("D"),
            ["teamId"] = e.Key.TeamId.ToString("D"), ["taskId"] = e.Key.TaskId.ToString("D"), ["resultId"] = a.ResultId.ToString("D"),
            ["expectedResultVersion"] = Version(e.ExpectedResultVersion), ["appealId"] = a.AppealId.ToString("D"),
            ["recheckRequestId"] = a.RecheckRequestId.ToString("D"), ["baseCheckId"] = a.BaseCheckId.ToString("D"),
            ["selectedReviewerId"] = a.SelectedReviewerId.ToString("D")
        };
        if (e.EscalationId is { } escalation) payload["escalationId"] = escalation.ToString("D");
        if (receipt.Applied) {
            var p = receipt.Publication!;
            payload["materialVersionId"] = e.MaterialVersionId.ToString("D"); payload["resultVersion"] = Version(p.ResultVersion);
            payload["previousOutcome"] = before.Source.Outcome.ToString(); payload["outcome"] = p.Source.Outcome.ToString();
            payload["previousScore"] = Exact(before.Score); payload["score"] = Exact(p.Score); payload["scoreRuleVersion"] = Version(p.Rule.Version);
            payload["changed"] = receipt.Changed; payload["appliedAt"] = now.UtcDateTime.ToString("O");
            payload["publicationState"] = "Published"; payload["basePublicationId"] = a.BasePublicationId.ToString("D");
            payload["publicationId"] = p.PublicationId.ToString("D");
            if (receipt.Changed) {
                var json = JsonSerializer.Serialize(p, Json);
                row.ResultVersion = p.ResultVersion; row.Score = p.Score; row.Outcome = p.Source.Outcome.ToString();
                row.PublicationId = p.PublicationId; row.Publication = json;
                db.Publications.Add(new() { PublicationId = p.PublicationId, ResultId = p.ResultId, ResultVersion = p.ResultVersion, Snapshot = json });
                snapshot.Version = checked(snapshot.Version + 1); snapshot.UpdatedAt = now;
            }
        } else {
            payload["actualResultVersion"] = Version(before.ResultVersion); payload["reasonCode"] = receipt.ReasonCode;
            payload["rejectedAt"] = now.UtcDateTime.ToString("O");
        }
        var message = Guid.NewGuid();
        var suffix = receipt.Applied ? "applied" : "rejected";
        var envelope = JsonSerializer.Serialize(new { messageId = message.ToString("D"), messageType = "matigr.results.result-evaluation-" + suffix,
            schemaVersion = 1, producer = "Results", occurredAt = now.UtcDateTime.ToString("O"), aggregateType = "EvaluationReceipt",
            aggregateId = evaluationId, aggregateVersion = "1", correlationId, causationId = messageId.ToString("D"), payload }, Json);
        db.Receipts.Add(new() { EvaluationId = e.CheckId, SemanticHash = hash, Status = status, ResultId = row.ResultId, Response = envelope });
        db.Inbox.Add(new() { Consumer = Consumer, MessageId = messageId, PayloadHash = hash, ProcessedAt = now });
        db.Outbox.Add(new() { MessageId = message, Envelope = envelope, RoutingKey = $"results.result-evaluation-{suffix}.appeal.v1", CreatedAt = now, NextAttemptAt = now });
        var failure = await db.Failures.SingleOrDefaultAsync(x => x.Consumer == Consumer && x.MessageId == messageId, ct);
        if (failure is not null) failure.State = "Resolved";
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(status, envelope);
    }
}
