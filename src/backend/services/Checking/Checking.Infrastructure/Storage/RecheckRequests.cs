using System.Globalization;
using System.Text.Json;
using Matigr.Checking.Application;
using Matigr.Checking.Domain;
using Matigr.Transport;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Checking.Infrastructure.Storage;

internal sealed class RecheckRequests(CheckingDbContext db, StorageProtection protection, TimeProvider clock) : IInboundHandler
{
    private const string Consumer = "Checking.RecheckRequested.v1";
    private static string Id(Guid id) => id.ToString("D");
    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    internal static RecheckRequest Read(JsonElement envelope)
    {
        var p = envelope.GetProperty("payload"); Guid Key(string name) => p.GetProperty(name).GetGuid();
        return new(Key("appealId"), Key("recheckRequestId"), Key("solutionId"), Key("materialVersionId"), Key("tournamentId"), Key("teamId"), Key("taskId"),
            Key("resultId"), long.Parse(p.GetProperty("expectedResultVersion").GetString()!, CultureInfo.InvariantCulture), Key("publicationId"), Key("baseCheckId"),
            Key("requestedReviewerId"), Enum.Parse<CheckOutcome>(p.GetProperty("baseOutcome").GetString()!, false),
            p.GetProperty("submittedAt").GetDateTimeOffset(), p.GetProperty("acceptedAt").GetDateTimeOffset());
    }
    public void Validate(JsonElement envelope)
    {
        try {
            var request = Read(envelope); request.Validate();
            if (envelope.GetProperty("aggregateId").GetString() != Id(request.AppealId) || envelope.GetProperty("messageId").GetGuid() == Guid.Empty)
                throw new ArgumentException("Invalid identity.");
        } catch (ArgumentException) { throw new InvalidMessageException("InvalidRecheckRequest"); }
    }
    public Task<InboundResult> HandleAsync(JsonElement envelope, CancellationToken ct) => HandleAsync(Read(envelope),
        envelope.GetProperty("messageId").GetGuid(), envelope.GetProperty("correlationId").GetString()!, ct);

    internal async Task<InboundResult> HandleAsync(RecheckRequest request, Guid messageId, string correlation, CancellationToken ct = default)
    {
        request.Validate(); var hash = request.SemanticHash();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var mutex = "request/" + Id(request.RequestId);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({mutex},0))", ct);
        var inbox = await db.Inbox.AsNoTracking().SingleOrDefaultAsync(x => x.Consumer == Consumer && x.MessageId == messageId, ct);
        if (inbox is not null && inbox.PayloadHash != hash) throw new InvalidMessageException("RecheckMessageIdentityConflict");
        var old = await db.RecheckRequestReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == request.RequestId, ct);
        if (old is not null) {
            if (old.RequestHash != hash) throw new InvalidMessageException("RecheckRequestIdentityConflict");
            if (inbox is null) db.Inbox.Add(new() { Consumer = Consumer, MessageId = messageId, PayloadHash = hash, ProcessedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(true);
        }
        var reviewer = Id(request.RequestedReviewerId);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({reviewer},0))", ct);
        var sources = await db.SolutionReferences.FromSqlInterpolated($"SELECT * FROM solution_references WHERE solution_id={request.SolutionId} FOR UPDATE").ToListAsync(ct);
        var s = sources.SingleOrDefault();
        if (s is null) return new(false, "SolutionDependencyUnavailable");
        var basis = await db.Checks.AsNoTracking().SingleOrDefaultAsync(x => x.CheckId == request.BaseCheckId, ct);
        if (basis is null || basis.State != CheckState.Completed) return new(false, "BaseCheckDependencyUnavailable");
        var reason = s.TournamentId != request.TournamentId || s.TeamId != request.TeamId || s.TaskId != request.TaskId
            || s.MaterialVersionId != request.MaterialVersionId || basis.SolutionId != request.SolutionId || basis.Outcome != request.BaseOutcome
            ? "BasisConflict" : basis.Origin == DecisionOrigin.SeniorEscalation ? "BaseNotAppealable" : null;
        if (reason is null && !await db.ReviewerScopes.AnyAsync(x => x.ReviewerId == request.RequestedReviewerId && x.TournamentId == request.TournamentId && x.Role == ReviewerRole.Reviewer, ct)) reason = "ReviewerScopeConflict";
        var existing = await db.Checks.AsNoTracking().SingleOrDefaultAsync(x => x.AppealId == request.AppealId, ct);
        string response; Guid? checkId = null;
        var now = clock.GetUtcNow(); now = now.AddTicks(-(now.Ticks % 10));
        if (existing is not null && reason is null) {
            if (existing.SolutionId != request.SolutionId || existing.BaseCheckId != request.BaseCheckId || existing.ResultId != request.ResultId
                || existing.BasePublicationId != request.PublicationId || existing.ExpectedResultVersion != request.ExpectedResultVersion
                || existing.SelectedReviewerId != request.RequestedReviewerId || existing.BaseOutcome != request.BaseOutcome) reason = "AppealBasisConflict";
            else {
                var original = await db.RecheckRequestReceipts.AsNoTracking().SingleAsync(x => x.RequestId == existing.RecheckRequestId, ct);
                response = original.ResponseEnvelope; checkId = existing.CheckId;
                await Persist(response, "Assigned", null, checkId); return new(true);
            }
        }
        if (reason is null && await db.Assignments.AnyAsync(x => x.ReviewerId == request.RequestedReviewerId && x.State == AssignmentState.Active, ct)) reason = "ReviewerBusy";
        if (reason is not null) {
            response = JsonSerializer.Serialize(new { messageId = Id(Guid.NewGuid()), messageType = "matigr.checking.recheck-request-rejected", schemaVersion = 1,
                producer = "Checking", occurredAt = Stamp(now), aggregateType = "RecheckRequest", aggregateId = Id(request.RequestId), aggregateVersion = "1",
                correlationId = correlation, causationId = Id(messageId), payload = new { appealId = Id(request.AppealId), recheckRequestId = Id(request.RequestId),
                    resultId = Id(request.ResultId), expectedResultVersion = Num(request.ExpectedResultVersion), requestedReviewerId = reviewer,
                    solutionId = Id(request.SolutionId), reasonCode = reason, rejectedAt = Stamp(now) } }, CheckingJson.Options);
            await Persist(response, "Rejected", reason, null); return new(true);
        }
        var baseAssignment = await db.Assignments.AsNoTracking().SingleAsync(x => x.AssignmentId == basis!.CurrentAssignmentId, ct);
        var snapshot = new SolutionReference(s.SolutionId, s.TournamentId, s.TeamId, s.TaskId, s.MaterialVersionId, s.MaterialRef, s.AcceptedAt, s.SourceVersion);
        var baseCheck = Check.RestoreAssigned(basis.CheckId, snapshot, basis.Kind, basis.ReviewRevision, basis.Version - 1, basis.EscalationId,
            basis.Kind == CheckKind.Initial ? null : new(basis.AppealId!.Value, basis.RecheckRequestId!.Value, basis.ResultId!.Value,
                basis.ExpectedResultVersion!.Value, basis.BaseCheckId!.Value, basis.BasePublicationId!.Value, basis.SelectedReviewerId!.Value, basis.BaseOutcome!.Value),
            baseAssignment.AssignmentId, new(baseAssignment.ReviewerId, basis.TournamentId, baseAssignment.Role), baseAssignment.StartedAt);
        baseCheck.Complete(baseAssignment.AssignmentId, baseAssignment.ReviewerId, basis.Outcome!.Value, basis.CompletedAt!.Value);
        var revision = checked(await db.Checks.Where(x => x.SolutionId == s.SolutionId).MaxAsync(x => x.ReviewRevision, ct) + 1);
        var a = new AppealBasis(request.AppealId, request.RequestId, request.ResultId, request.ExpectedResultVersion, request.BaseCheckId, request.PublicationId, request.RequestedReviewerId, request.BaseOutcome);
        var check = Check.CreateAppeal(Guid.NewGuid(), snapshot, revision, a, baseCheck, Guid.NewGuid(), new(request.RequestedReviewerId, request.TournamentId, ReviewerRole.Reviewer), now);
        checkId = check.CheckId; var assignment = check.CurrentAssignment!;
        var capability = protection.IssueCapability(assignment.AssignmentId, request.RequestedReviewerId);
        db.Checks.Add(new() { CheckId = check.CheckId, SolutionId = s.SolutionId, TournamentId = s.TournamentId, Kind = CheckKind.Appeal,
            ReviewRevision = revision, State = check.State, Version = check.Version, CreatedAt = now, CurrentAssignmentId = assignment.AssignmentId,
            AppealId = request.AppealId, RecheckRequestId = request.RequestId, ResultId = request.ResultId, ExpectedResultVersion = request.ExpectedResultVersion,
            BaseCheckId = request.BaseCheckId, BasePublicationId = request.PublicationId, SelectedReviewerId = request.RequestedReviewerId, BaseOutcome = request.BaseOutcome });
        db.Assignments.Add(new() { AssignmentId = assignment.AssignmentId, CheckId = check.CheckId, ReviewerId = request.RequestedReviewerId,
            TournamentId = s.TournamentId, Role = ReviewerRole.Reviewer, State = AssignmentState.Active, StartedAt = now,
            CapabilityHash = capability.Hash, CapabilityCiphertext = capability.Ciphertext });
        db.CheckHistory.Add(new() { HistoryId = Guid.NewGuid(), CheckId = check.CheckId, Version = check.Version, TransitionType = "AppealAssigned",
            ActorId = request.RequestedReviewerId, AssignmentId = assignment.AssignmentId, ToState = check.State, OccurredAt = now });
        response = JsonSerializer.Serialize(new { messageId = Id(Guid.NewGuid()), messageType = "matigr.checking.recheck-assigned", schemaVersion = 1,
            producer = "Checking", occurredAt = Stamp(now), aggregateType = "Check", aggregateId = Id(check.CheckId), aggregateVersion = Num(check.Version),
            correlationId = correlation, causationId = Id(messageId), payload = new { appealId = Id(request.AppealId), recheckRequestId = Id(request.RequestId),
                checkId = Id(check.CheckId), selectedReviewerId = reviewer, reviewerId = reviewer, assignmentId = Id(assignment.AssignmentId), assignmentKind = "Selected",
                solutionId = Id(s.SolutionId), materialVersionId = Id(s.MaterialVersionId), tournamentId = Id(s.TournamentId), teamId = Id(s.TeamId), taskId = Id(s.TaskId),
                resultId = Id(request.ResultId), publicationId = Id(request.PublicationId), expectedResultVersion = Num(request.ExpectedResultVersion),
                reviewRevision = Num(revision), createdAt = Stamp(now), assignedAt = Stamp(now) } }, CheckingJson.Options);
        await Persist(response, "Assigned", null, checkId); return new(true);

        async Task Persist(string envelope, string outcome, string? code, Guid? assignedCheck)
        {
            db.RecheckRequestReceipts.Add(new() { RequestId = request.RequestId, AppealId = request.AppealId, RequestHash = hash, Outcome = outcome,
                CheckId = assignedCheck, ReasonCode = code, ResponseEnvelope = envelope, RecordedAt = now });
            db.Inbox.Add(new() { Consumer = Consumer, MessageId = messageId, PayloadHash = hash, ProcessedAt = now });
            using var json = JsonDocument.Parse(envelope); var id = json.RootElement.GetProperty("messageId").GetGuid();
            if (!await db.Outbox.AnyAsync(x => x.MessageId == id, ct)) db.Outbox.Add(new() { MessageId = id, Envelope = envelope,
                RoutingKey = outcome == "Assigned" ? "checking.recheck-assigned.v1" : "checking.recheck-request-rejected.v1",
                CorrelationId = request.AppealId, CreatedAt = now, NextAttemptAt = now });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
    }
}
