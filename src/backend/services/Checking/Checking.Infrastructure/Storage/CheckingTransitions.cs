using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matigr.Checking.Application;
using Matigr.Checking.Application.Identity;
using Matigr.Checking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Checking.Infrastructure.Storage;

internal sealed partial class CheckingWork
{
    public async Task<CheckView> TransitionAsync(AuthenticatedSubject subject, Guid checkId, Guid assignmentId, string capability,
        string? outcome, long expectedVersion, string key, string correlationId, CancellationToken ct)
    {
        var reviewer = Reviewer(subject);
        if (outcome is not null and not ("Correct" or "Incorrect")) throw new CheckingOperationException(400, "InvalidRequest");
        var operation = outcome is null ? "Refuse" : "Complete";
        var storageKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        // If-Match is a precondition, not business payload. Lost replies replay before stale ETag.
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { checkId, assignmentId, capability, outcome }, CheckingJson.Options)));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var actor = Id(reviewer);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({actor}, 0))", ct);
        var rows = await db.Checks.FromSqlInterpolated($"SELECT * FROM checks WHERE check_id = {checkId} FOR UPDATE").ToListAsync(ct);
        var row = rows.SingleOrDefault() ?? throw new CheckingOperationException(404, "CheckNotFound");
        await Scope(subject, row.TournamentId, null, ct);
        // Even after completion/reassignment, a replay requires the original owner and capability.
        var a = await db.Assignments.SingleOrDefaultAsync(x => x.AssignmentId == assignmentId && x.CheckId == checkId, ct);
        if (a is null || a.ReviewerId != reviewer || !StorageProtection.MatchesHash(capability, a.CapabilityHash))
            throw new CheckingOperationException(403, "Forbidden");
        await Scope(subject, row.TournamentId, a.Role == ReviewerRole.Senior ? "senior" : "general", ct);
        var receipt = await db.HttpIdempotency.AsNoTracking().SingleOrDefaultAsync(x => x.SubjectId == reviewer
            && x.Operation == operation && x.Key == storageKey, ct);
        if (receipt is not null)
        {
            if (receipt.RequestHash != hash) throw new CheckingOperationException(409, "IdempotencyConflict");
            return JsonSerializer.Deserialize<CheckView>(protection.RestoreHttpResponse(reviewer, operation, storageKey,
                receipt.ResponseCiphertext), CheckingJson.Options)!;
        }
        if (row.Version != expectedVersion) throw new CheckingOperationException(412, "CheckVersionConflict");
        if (a.State != AssignmentState.Active || row.CurrentAssignmentId != assignmentId
            || row.State is not (CheckState.Assigned or CheckState.SeniorAssigned)
            || (outcome is null && row.State != CheckState.Assigned))
            throw new CheckingOperationException(409, "CheckStateConflict");
        var source = await db.SolutionReferences.AsNoTracking().SingleAsync(x => x.SolutionId == row.SolutionId, ct);
        var check = Check.RestoreAssigned(row.CheckId, Snapshot(source), row.Kind, row.ReviewRevision, row.Version,
            row.EscalationId, Basis(row), assignmentId, new(reviewer, row.TournamentId, a.Role), a.StartedAt);
        var now = clock.GetUtcNow(); now = now.AddTicks(-(now.Ticks % 10));
        var from = row.State;
        if (outcome is null)
        {
            row.EscalationId = check.RefuseToSenior(assignmentId, reviewer, Guid.NewGuid(), now);
            row.CurrentAssignmentId = null;
        }
        else
        {
            row.Outcome = Enum.Parse<CheckOutcome>(outcome);
            row.Origin = check.Complete(assignmentId, reviewer, row.Outcome.Value, now);
            row.CompletedAt = now;
        }
        row.State = check.State;
        a.State = check.State == CheckState.Completed ? AssignmentState.Completed : AssignmentState.Refused;
        a.EndedAt = now;
        db.CheckHistory.Add(new CheckHistoryRow { HistoryId = Guid.NewGuid(), CheckId = checkId, Version = check.Version,
            TransitionType = operation, ActorId = reviewer, AssignmentId = assignmentId, EscalationId = row.EscalationId,
            FromState = from, ToState = row.State, Outcome = row.Outcome, OccurredAt = now });
        await db.SaveChangesAsync(ct);
        if (outcome is not null || row.Kind == CheckKind.Appeal) TransitionFact(row, source, a, now, correlationId);
        var view = View(row, source);
        db.HttpIdempotency.Add(new HttpIdempotencyRow { SubjectId = reviewer, Operation = operation, Key = storageKey,
            RequestHash = hash, StatusCode = 200, ETag = $"\"{view.Version}\"", RecordedAt = now,
            ResponseCiphertext = protection.ProtectHttpResponse(reviewer, operation, storageKey, JsonSerializer.Serialize(view, CheckingJson.Options)) });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return view;
    }

    private void TransitionFact(CheckRow c, SolutionReferenceRow s, AssignmentRow a, DateTimeOffset now, string commandId)
    {
        var completed = c.State == CheckState.Completed;
        var name = completed ? "check-completed" : "recheck-escalated";
        var payload = new Dictionary<string, object?> { ["checkId"] = Id(c.CheckId), ["tournamentId"] = Id(s.TournamentId),
            ["teamId"] = Id(s.TeamId), ["taskId"] = Id(s.TaskId), ["expectedResultVersion"] = Number(c.ExpectedResultVersion ?? 0) };
        if (completed)
        {
            payload["solutionId"] = Id(s.SolutionId); payload["materialVersionId"] = Id(s.MaterialVersionId);
            payload["kind"] = c.Kind.ToString(); payload["reviewRevision"] = Number(c.ReviewRevision);
            payload["outcome"] = c.Outcome!.Value.ToString(); payload["decisionOrigin"] = c.Origin!.Value.ToString();
            payload["reviewerId"] = Id(a.ReviewerId); payload["assignmentId"] = Id(a.AssignmentId); payload["completedAt"] = Stamp(now);
        }
        else
        {
            payload["releasedReviewerId"] = Id(a.ReviewerId); payload["releasedAssignmentId"] = Id(a.AssignmentId);
            payload["escalatedAt"] = Stamp(now);
        }
        if (c.EscalationId is { } escalation) payload["escalationId"] = Id(escalation);
        if (c.AppealId is { } appeal)
        {
            payload["appealId"] = Id(appeal); payload["recheckRequestId"] = Id(c.RecheckRequestId!.Value);
            payload["selectedReviewerId"] = Id(c.SelectedReviewerId!.Value); payload["resultId"] = Id(c.ResultId!.Value);
            if (completed) { payload["baseCheckId"] = Id(c.BaseCheckId!.Value); payload["publicationId"] = Id(c.BasePublicationId!.Value); }
        }
        var messageId = Guid.NewGuid();
        var envelope = new { messageId = Id(messageId), messageType = "matigr.checking." + name, schemaVersion = 1,
            producer = "Checking", occurredAt = Stamp(now), aggregateType = "Check", aggregateId = Id(c.CheckId),
            aggregateVersion = Number(c.Version), correlationId = Id(c.AppealId ?? c.CheckId), causationId = commandId, payload };
        db.Outbox.Add(new OutboxRow { MessageId = messageId, Envelope = JsonSerializer.Serialize(envelope, CheckingJson.Options),
            RoutingKey = completed ? $"checking.check-completed.{c.Kind.ToString().ToLowerInvariant()}.v1" : "checking.recheck-escalated.v1",
            CorrelationId = c.AppealId ?? c.CheckId, CreatedAt = now, NextAttemptAt = now });
    }
}
