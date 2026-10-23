using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matigr.Checking.Application;
using Matigr.Checking.Application.Identity;
using Matigr.Checking.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Matigr.Checking.Infrastructure.Storage;

internal sealed class CheckingWork(CheckingDbContext db, StorageProtection protection, IReadOnlyMaterialStore materials,
    TimeProvider clock, QueueReadOptions options) : ICheckingWork
{
    private const string TakeOperation = "TakeNext";
    private static string Id(Guid id) => id.ToString("D");
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private sealed record Cursor(Guid TournamentId, string Queue, DateTimeOffset CreatedAt, Guid CheckId);

    private static Guid Reviewer(AuthenticatedSubject subject)
    {
        if (!subject.IsReviewer || !Guid.TryParse(subject.SubjectId, out var id) || id == Guid.Empty)
            throw new CheckingOperationException(403, "Forbidden");
        return id;
    }

    private async Task<ReviewerScope> Scope(AuthenticatedSubject subject, Guid tournament, string? queue, CancellationToken ct)
    {
        var reviewer = Reviewer(subject);
        if (queue is not null and not ("general" or "senior")) throw new CheckingOperationException(400, "InvalidRequest");
        if (!subject.CanReview(Id(tournament), queue == "senior")) throw new CheckingOperationException(403, "Forbidden");
        var role = queue == "senior" ? ReviewerRole.Senior : ReviewerRole.Reviewer;
        var scoped = db.ReviewerScopes.AsNoTracking().Where(x => x.ReviewerId == reviewer && x.TournamentId == tournament);
        if (!await (queue is null ? scoped : scoped.Where(x => x.Role == role)).AnyAsync(ct))
            throw new CheckingOperationException(403, "Forbidden");
        return new(reviewer, tournament, role);
    }

    private static SolutionReference Snapshot(SolutionReferenceRow s) => new(s.SolutionId, s.TournamentId, s.TeamId,
        s.TaskId, s.MaterialVersionId, s.MaterialRef, s.AcceptedAt, s.SourceVersion);
    private static AppealBasis? Basis(CheckRow c) => c.Kind == CheckKind.Initial ? null : new(c.AppealId!.Value,
        c.RecheckRequestId!.Value, c.ResultId!.Value, c.ExpectedResultVersion!.Value, c.BaseCheckId!.Value,
        c.BasePublicationId!.Value, c.SelectedReviewerId!.Value, c.BaseOutcome!.Value);
    private static CheckView View(CheckRow c, SolutionReferenceRow s) => new(Id(c.CheckId), Id(s.SolutionId),
        Id(s.MaterialVersionId), Id(s.TournamentId), Id(s.TeamId), Id(s.TaskId), c.Kind.ToString(), c.State.ToString(),
        Number(c.Version), Number(c.ReviewRevision), Stamp(c.CreatedAt), c.Outcome?.ToString(), c.Origin?.ToString());
    private AssignmentView Owned(CheckRow c, SolutionReferenceRow s, AssignmentRow a) => new(View(c, s),
        Id(a.AssignmentId), Id(a.ReviewerId), Stamp(a.StartedAt),
        protection.RestoreCapability(a.AssignmentId, a.ReviewerId, a.CapabilityHash, a.CapabilityCiphertext));

    public async Task<QueuePage> QueueAsync(AuthenticatedSubject subject, Guid tournamentId, string queue, string? cursor, CancellationToken ct)
    {
        await Scope(subject, tournamentId, queue, ct);
        var state = queue == "senior" ? CheckState.SeniorQueued : CheckState.Queued;
        var query = db.Checks.AsNoTracking().Where(x => x.TournamentId == tournamentId && x.State == state);
        if (cursor is not null)
        {
            Cursor position;
            try
            {
                var encoded = cursor.Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
                position = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(encoded), CheckingJson.Options)
                    ?? throw new JsonException();
            }
            catch (Exception error) when (error is FormatException or JsonException)
            { throw new CheckingOperationException(400, "InvalidRequest"); }
            if (position.TournamentId != tournamentId || position.Queue != queue || position.CheckId == Guid.Empty || position.CreatedAt.Offset != TimeSpan.Zero)
                throw new CheckingOperationException(400, "InvalidRequest");
            var stateName = state.ToString();
            query = db.Checks.FromSqlInterpolated($"""
                SELECT * FROM checks WHERE tournament_id = {tournamentId} AND state = {stateName}
                AND (created_at > {position.CreatedAt} OR (created_at = {position.CreatedAt} AND check_id > {position.CheckId}))
                """).AsNoTracking();
        }
        var checks = await query.OrderBy(x => x.CreatedAt).ThenBy(x => x.CheckId).Take(options.PageSize + 1).ToListAsync(ct);
        var page = checks.Take(options.PageSize).ToArray();
        var ids = page.Select(x => x.SolutionId).ToArray();
        var solutions = await db.SolutionReferences.AsNoTracking().Where(x => ids.Contains(x.SolutionId)).ToDictionaryAsync(x => x.SolutionId, ct);
        var items = page.Select(c => new QueueItem(Id(c.CheckId), Id(c.SolutionId), Id(c.TournamentId),
            Id(solutions[c.SolutionId].TeamId), Id(solutions[c.SolutionId].TaskId), c.Kind.ToString(), c.State.ToString(), Stamp(c.CreatedAt))).ToArray();
        var next = checks.Count > options.PageSize ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new Cursor(tournamentId, queue, page[^1].CreatedAt, page[^1].CheckId), CheckingJson.Options)).TrimEnd('=').Replace('+', '-').Replace('/', '_') : null;
        return new(Id(tournamentId), queue, items, next);
    }

    public async Task<TakeNextReply> TakeNextAsync(AuthenticatedSubject subject, Guid tournamentId, string queue, string key, string correlationId, CancellationToken ct)
    {
        var scope = await Scope(subject, tournamentId, queue, ct);
        if (string.IsNullOrEmpty(key)) throw new CheckingOperationException(400, "IdempotencyKeyRequired");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{tournamentId:D}\n{queue}")));
        // Bound the database index representation, not the accepted HTTP key length.
        var storageKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        try { return await Take(scope, storageKey, hash, correlationId, ct); }
        catch (Exception error) when (error is DbUpdateConcurrencyException || error is DbUpdateException
            { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "IX_assignments_reviewer_id" or "IX_assignments_check_id" } }
            || error is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
        {
            // A writer outside this HTTP mutex may have won occupancy. Roll back, then resolve
            // once using the same operation identity. The UNIQUE constraint remains authoritative.
            db.ChangeTracker.Clear();
            return await Take(scope, storageKey, hash, correlationId, ct);
        }
    }

    private async Task<TakeNextReply> Take(ReviewerScope scope, string key, string hash, string correlationId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var reviewer = Id(scope.ReviewerId);
        // Global per reviewer, independent of tournament/queue/tab. This is a transaction mutex,
        // never a lease/timeout on business assignments. Other writers must still obey UNIQUE.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({reviewer}, 0))", ct);
        var receipt = await db.HttpIdempotency.AsNoTracking().SingleOrDefaultAsync(x => x.SubjectId == scope.ReviewerId
            && x.Operation == TakeOperation && x.Key == key, ct);
        if (receipt is not null)
        {
            if (receipt.RequestHash != hash) return new(null, "IdempotencyConflict", correlationId);
            return JsonSerializer.Deserialize<TakeNextReply>(protection.RestoreHttpResponse(scope.ReviewerId, TakeOperation, key,
                receipt.ResponseCiphertext), CheckingJson.Options) ?? throw new InvalidOperationException("Invalid saved response.");
        }
        TakeNextReply reply;
        if (await db.Assignments.AnyAsync(x => x.ReviewerId == scope.ReviewerId && x.State == AssignmentState.Active, ct))
            reply = new(null, "ReviewerBusy", correlationId);
        else
        {
            var stateName = scope.Role == ReviewerRole.Senior ? "SeniorQueued" : "Queued";
            var rows = await db.Checks.FromSqlInterpolated($"""
                SELECT * FROM checks WHERE tournament_id = {scope.TournamentId} AND state = {stateName}
                ORDER BY created_at, check_id LIMIT 1 FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct);
            if (rows.Count == 0) reply = new(null, null, correlationId);
            else
            {
                var row = rows[0];
                var source = await db.SolutionReferences.AsNoTracking().SingleAsync(x => x.SolutionId == row.SolutionId, ct);
                var check = Check.RestoreQueued(row.CheckId, Snapshot(source), row.Kind, row.ReviewRevision, row.Version, row.EscalationId, Basis(row));
                var now = clock.GetUtcNow();
                now = now.AddTicks(-(now.Ticks % 10));
                var assigned = check.Assign(Guid.NewGuid(), scope, now);
                var capability = protection.IssueCapability(assigned.AssignmentId, scope.ReviewerId);
                var assignment = new AssignmentRow { AssignmentId = assigned.AssignmentId, CheckId = row.CheckId,
                    ReviewerId = scope.ReviewerId, TournamentId = scope.TournamentId, Role = scope.Role,
                    StartedAt = assigned.StartedAt, State = AssignmentState.Active,
                    CapabilityHash = capability.Hash, CapabilityCiphertext = capability.Ciphertext };
                var from = row.State;
                row.State = check.State;
                row.CurrentAssignmentId = assigned.AssignmentId;
                db.Assignments.Add(assignment);
                db.CheckHistory.Add(new CheckHistoryRow { HistoryId = Guid.NewGuid(), CheckId = row.CheckId,
                    Version = check.Version, TransitionType = "Assigned", ActorId = scope.ReviewerId,
                    AssignmentId = assigned.AssignmentId, EscalationId = row.EscalationId,
                    FromState = from, ToState = check.State, OccurredAt = now });
                await db.SaveChangesAsync(ct); // CAS, assignment, history remain inside the transaction.
                if (row.Kind == CheckKind.Appeal) QueueSeniorAssigned(row, source, assignment, now, correlationId);
                reply = new(Owned(row, source, assignment), null, correlationId);
            }
        }
        db.HttpIdempotency.Add(new HttpIdempotencyRow { SubjectId = scope.ReviewerId, Operation = TakeOperation,
            Key = key, RequestHash = hash, StatusCode = reply.Status, ETag = reply.ETag, RecordedAt = clock.GetUtcNow(),
            ResponseCiphertext = protection.ProtectHttpResponse(scope.ReviewerId, TakeOperation, key,
                JsonSerializer.Serialize(reply, CheckingJson.Options)) });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return reply;
    }

    private void QueueSeniorAssigned(CheckRow c, SolutionReferenceRow s, AssignmentRow a, DateTimeOffset now, string commandId)
    {
        var messageId = Guid.NewGuid();
        var envelope = new { messageId = Id(messageId), messageType = "matigr.checking.recheck-assigned", schemaVersion = 1,
            producer = "Checking", occurredAt = Stamp(now), aggregateType = "Check", aggregateId = Id(c.CheckId),
            aggregateVersion = Number(c.Version), correlationId = Id(c.AppealId!.Value), causationId = commandId,
            payload = new { recheckRequestId = Id(c.RecheckRequestId!.Value), appealId = Id(c.AppealId.Value), checkId = Id(c.CheckId),
                selectedReviewerId = Id(c.SelectedReviewerId!.Value), reviewerId = Id(a.ReviewerId), assignmentId = Id(a.AssignmentId),
                assignmentKind = "SeniorEscalation", solutionId = Id(s.SolutionId), materialVersionId = Id(s.MaterialVersionId),
                tournamentId = Id(s.TournamentId), teamId = Id(s.TeamId), taskId = Id(s.TaskId), resultId = Id(c.ResultId!.Value),
                publicationId = Id(c.BasePublicationId!.Value), expectedResultVersion = Number(c.ExpectedResultVersion!.Value),
                reviewRevision = Number(c.ReviewRevision), createdAt = Stamp(c.CreatedAt), assignedAt = Stamp(a.StartedAt),
                escalationId = Id(c.EscalationId!.Value) } };
        db.Outbox.Add(new OutboxRow { MessageId = messageId, Envelope = JsonSerializer.Serialize(envelope, CheckingJson.Options),
            RoutingKey = "checking.recheck-assigned.v1", CorrelationId = c.AppealId.Value, CreatedAt = now, NextAttemptAt = now });
    }

    public async Task<CheckView> CheckAsync(AuthenticatedSubject subject, Guid checkId, CancellationToken ct)
    {
        Reviewer(subject);
        var c = await db.Checks.AsNoTracking().SingleOrDefaultAsync(x => x.CheckId == checkId, ct)
            ?? throw new CheckingOperationException(404, "CheckNotFound");
        if (!subject.CanReview(Id(c.TournamentId))) throw new CheckingOperationException(404, "CheckNotFound");
        await Scope(subject, c.TournamentId, null, ct);
        return View(c, await db.SolutionReferences.AsNoTracking().SingleAsync(x => x.SolutionId == c.SolutionId, ct));
    }

    public async Task<AssignmentView?> MyAssignmentAsync(AuthenticatedSubject subject, CancellationToken ct)
    {
        var reviewer = Reviewer(subject);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var a = await db.Assignments.AsNoTracking().SingleOrDefaultAsync(x => x.ReviewerId == reviewer && x.State == AssignmentState.Active, ct);
        if (a is null) return null;
        var c = await db.Checks.AsNoTracking().SingleAsync(x => x.CheckId == a.CheckId, ct);
        await Scope(subject, c.TournamentId, a.Role == ReviewerRole.Senior ? "senior" : "general", ct);
        if (c.CurrentAssignmentId != a.AssignmentId || c.State is not (CheckState.Assigned or CheckState.SeniorAssigned))
            throw new CheckingOperationException(503, "DependencyUnavailable");
        if ((a.Role == ReviewerRole.Senior) != (c.State == CheckState.SeniorAssigned))
            throw new CheckingOperationException(503, "DependencyUnavailable");
        return Owned(c, await db.SolutionReferences.AsNoTracking().SingleAsync(x => x.SolutionId == c.SolutionId, ct), a);
    }

    private Task<bool> IsOwner(Guid reviewer, Guid checkId, CancellationToken ct) => (from a in db.Assignments
        join c in db.Checks on a.CheckId equals c.CheckId
        where a.ReviewerId == reviewer && a.State == AssignmentState.Active && c.CheckId == checkId
            && c.CurrentAssignmentId == a.AssignmentId && ((c.State == CheckState.Assigned && a.Role == ReviewerRole.Reviewer)
                || (c.State == CheckState.SeniorAssigned && a.Role == ReviewerRole.Senior))
        select a.AssignmentId).AnyAsync(ct);

    public async Task<Stream> MaterialAsync(AuthenticatedSubject subject, Guid checkId, CancellationToken ct)
    {
        var reviewer = Reviewer(subject);
        var c = await db.Checks.AsNoTracking().SingleOrDefaultAsync(x => x.CheckId == checkId, ct)
            ?? throw new CheckingOperationException(404, "CheckNotFound");
        await Scope(subject, c.TournamentId, c.State == CheckState.SeniorAssigned ? "senior" : "general", ct);
        if (!await IsOwner(reviewer, checkId, ct)) throw new CheckingOperationException(403, "Forbidden");
        var s = await db.SolutionReferences.AsNoTracking().SingleAsync(x => x.SolutionId == c.SolutionId, ct);
        // No SQL transaction is held during filesystem IO.
        var stream = await materials.OpenAsync(Snapshot(s), ct);
        try
        {
            if (!await IsOwner(reviewer, checkId, ct)) throw new CheckingOperationException(403, "Forbidden");
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
}
