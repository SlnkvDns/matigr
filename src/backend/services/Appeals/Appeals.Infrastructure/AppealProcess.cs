using System.Globalization;
using System.Text;
using System.Text.Json;
using Matigr.Appeals.Domain;
using Matigr.Transport;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Appeals.Infrastructure;

internal sealed partial class AppealProcess(AppealsDbContext db) : IInboundHandler
{
    internal const string Consumer = "Appeals.Process.v1";
    private static Guid Id(JsonElement p, string name)
    {
        var id = p.GetProperty(name).GetGuid(); if (id == Guid.Empty) throw new InvalidMessageException("InvalidProcessIdentity"); return id;
    }
    private static long Version(JsonElement p, string name) => long.Parse(p.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
    public void Validate(JsonElement envelope) { Id(envelope, "messageId"); Id(envelope.GetProperty("payload"), "appealId"); }
    private static string SemanticHash(JsonElement envelope)
    {
        using var normalized = JsonDocument.Parse(JsonSerializer.Serialize(new { type = envelope.GetProperty("messageType").GetString(),
            aggregateType = envelope.GetProperty("aggregateType").GetString(), aggregateId = envelope.GetProperty("aggregateId").GetString(),
            version = envelope.GetProperty("aggregateVersion").GetString(), payload = envelope.GetProperty("payload") }));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(normalized.RootElement, writer);
        return AppealStorage.Hash(Encoding.UTF8.GetString(buffer.ToArray()));
        static void Write(JsonElement value, Utf8JsonWriter writer) {
            if (value.ValueKind == JsonValueKind.Object) { writer.WriteStartObject(); foreach (var p in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal)) { writer.WritePropertyName(p.Name); Write(p.Value, writer); } writer.WriteEndObject(); }
            else if (value.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(item, writer); writer.WriteEndArray(); }
            else value.WriteTo(writer);
        }
    }
    public async Task<InboundResult> HandleAsync(JsonElement envelope, CancellationToken ct)
    {
        var message = Id(envelope, "messageId"); var p = envelope.GetProperty("payload"); var appeal = Id(p, "appealId"); var hash = SemanticHash(envelope);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var mutex = Consumer + "/" + message.ToString("D");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({mutex},0))", ct);
        var old = await db.Inbox.AsNoTracking().SingleOrDefaultAsync(x => x.Consumer == Consumer && x.MessageId == message, ct);
        if (old is not null) {
            if (old.PayloadHash != hash) throw new InvalidMessageException("ProcessMessageIdentityConflict");
            return new(true);
        }
        var row = (await db.Appeals.FromSqlInterpolated($"SELECT * FROM appeals WHERE appeal_id={appeal} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if (row is null) return new(false, "AppealDependencyUnavailable");
        var request = Id(p, "recheckRequestId");
        var attempt = await db.Attempts.SingleOrDefaultAsync(x => x.RequestId == request && x.AppealId == appeal, ct);
        if (attempt is null) throw new InvalidMessageException("UnknownSelectionAttempt");
        var before = AppealStorage.Read(row); var next = before;
        if (before.Attempt?.RequestId == request) {
            try {
                var name = envelope.GetProperty("messageType").GetString();
                if (name == "matigr.checking.recheck-request-rejected") {
                    if (Id(p, "requestedReviewerId") != attempt.ReviewerId || Id(p, "solutionId") != before.Basis.SolutionId
                        || Id(p, "resultId") != before.Basis.ResultId || Version(p, "expectedResultVersion") != before.Basis.ResultVersion)
                        throw new InvalidMessageException("RecheckReplyCorrelationConflict");
                    next = before.RequestRejected(request, p.GetProperty("reasonCode").GetString()!);
                } else if (name == "matigr.checking.recheck-assigned") {
                    CheckBasis(p, before);
                    if (Id(p, "selectedReviewerId") != attempt.ReviewerId) throw new InvalidMessageException("SelectedReviewerConflict");
                    var escalation = p.TryGetProperty("escalationId", out _) ? Id(p, "escalationId") : (Guid?)null;
                    var kind = p.GetProperty("assignmentKind").GetString();
                    if ((kind == "SeniorEscalation") != escalation.HasValue || (kind == "Selected" && Id(p, "reviewerId") != attempt.ReviewerId))
                        throw new InvalidMessageException("AssignmentLineageConflict");
                    next = before.Observe(new(request, Id(p, "checkId"), attempt.ReviewerId, Version(envelope, "aggregateVersion"),
                        Id(p, "assignmentId"), Id(p, "reviewerId"), escalation), escalation.HasValue ? AppealState.AwaitingSeniorReview : AppealState.RecheckPending);
                } else return new(false, "FinalProcessHandlerPending");
            } catch (AppealRuleException e) { throw new InvalidMessageException(e.Message); }
        }
        if (next != before) {
            AppealStorage.Update(row, next); attempt.State = next.Attempt!.State.ToString(); attempt.ReasonCode = next.Conflict;
            db.History.Add(new() { HistoryId = Guid.NewGuid(), AppealId = appeal, Version = next.Version, State = next.State.ToString(),
                RequestId = request, CheckId = next.Progress?.CheckId ?? next.Receipt?.EvaluationId, Snapshot = row.Snapshot, OccurredAt = DateTimeOffset.UtcNow });
        }
        db.Inbox.Add(new() { Consumer = Consumer, MessageId = message, PayloadHash = hash, ProcessedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(true);
    }
    private static void CheckBasis(JsonElement p, Appeal appeal)
    {
        var b = appeal.Basis;
        if (Id(p, "resultId") != b.ResultId || Version(p, "expectedResultVersion") != b.ResultVersion || Id(p, "solutionId") != b.SolutionId
            || Id(p, "materialVersionId") != b.MaterialVersionId || Id(p, "tournamentId") != b.TournamentId || Id(p, "teamId") != b.TeamId || Id(p, "taskId") != b.TaskId
            || Id(p, "publicationId") != b.PublicationId) throw new InvalidMessageException("ProcessBasisConflict");
    }
}
