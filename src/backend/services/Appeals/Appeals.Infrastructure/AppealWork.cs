using System.Text.Json;
using Matigr.Appeals.Application;
using Matigr.Appeals.Domain;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Appeals.Infrastructure;

internal sealed class AppealWork(AppealsDbContext db) : IAppealWork
{
    private static bool Jury(AppealActor actor, Guid tournament) => actor.Has("Jury", tournament) || actor.Has("Senior", tournament);
    private static bool Visible(AppealActor actor, AppealRow row) => Jury(actor, row.TournamentId)
        || actor.Has("Team", row.TournamentId) && actor.TeamId == row.TeamId;
    public async Task<AppealReply> ListAsync(AppealActor actor, Guid tournament, bool own, CancellationToken ct)
    {
        if (own ? !actor.Has("Team", tournament) || actor.TeamId is null : !Jury(actor, tournament)) throw new AppealOperationException(403, "Forbidden");
        var query = db.Appeals.AsNoTracking().Where(x => x.TournamentId == tournament);
        query = own ? query.Where(x => x.TeamId == actor.TeamId) : query.Where(x => x.State == "Registered");
        var rows = await query.OrderBy(x => x.SubmittedAt).ThenBy(x => x.AppealId).ToListAsync(ct);
        return new(200, JsonSerializer.Serialize(new { tournamentId = tournament.ToString("D"), items = rows.Select(x => new {
            appealId = x.AppealId.ToString("D"), tournamentId = x.TournamentId.ToString("D"), teamId = x.TeamId.ToString("D"), taskId = x.TaskId.ToString("D"),
            state = x.State, submittedAt = AppealStorage.Stamp(x.SubmittedAt), version = AppealStorage.Number(x.Version) }) }, AppealStorage.Json));
    }
    public async Task<AppealReply> GetAsync(AppealActor actor, Guid appeal, CancellationToken ct)
    {
        var row = await db.Appeals.AsNoTracking().SingleOrDefaultAsync(x => x.AppealId == appeal, ct);
        if (row is null || !Visible(actor, row)) throw new AppealOperationException(404, "NotFound");
        return new(200, AppealStorage.View(AppealStorage.Read(row)), $"\"{AppealStorage.Number(row.Version)}\"");
    }
    public async Task<AppealReply> ClaimAsync(AppealActor actor, Guid appeal, long expectedVersion, string key, CancellationToken ct)
    {
        if (actor.SubjectId == Guid.Empty || string.IsNullOrWhiteSpace(key) || expectedVersion < 1) throw new AppealOperationException(400, "InvalidRequest");
        const string operation = "Claim"; var storageKey = AppealStorage.Hash(key); var hash = AppealStorage.Hash(appeal.ToString("D"));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var mutex = $"{actor.SubjectId:D}/{operation}/{storageKey}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({mutex},0))", ct);
        var row = (await db.Appeals.FromSqlInterpolated($"SELECT * FROM appeals WHERE appeal_id={appeal} FOR UPDATE").ToListAsync(ct)).SingleOrDefault()
            ?? throw new AppealOperationException(404, "NotFound");
        if (!Jury(actor, row.TournamentId)) throw new AppealOperationException(403, "Forbidden");
        var old = await db.HttpReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.SubjectId == actor.SubjectId && x.Operation == operation && x.Key == storageKey, ct);
        if (old is not null) {
            if (old.RequestHash != hash) throw new AppealOperationException(409, "IdempotencyConflict");
            return new(old.StatusCode, old.Response, old.ETag, old.Location);
        }
        if (row.Version != expectedVersion) throw new AppealOperationException(412, "AppealVersionConflict");
        var before = AppealStorage.Read(row); var now = DateTimeOffset.UtcNow; now = now.AddTicks(-(now.Ticks % 10));
        Appeal selected;
        try { selected = before.Claim(Guid.NewGuid(), actor.SubjectId, now); }
        catch (AppealRuleException) { throw new AppealOperationException(409, "AppealNotRegistered"); }
        var b = selected.Basis; var attempt = selected.Attempt!;
        var payload = new { solutionId = b.SolutionId.ToString("D"), materialVersionId = b.MaterialVersionId.ToString("D"), tournamentId = b.TournamentId.ToString("D"),
            teamId = b.TeamId.ToString("D"), taskId = b.TaskId.ToString("D"), appealId = appeal.ToString("D"), recheckRequestId = attempt.RequestId.ToString("D"),
            resultId = b.ResultId.ToString("D"), expectedResultVersion = AppealStorage.Number(b.ResultVersion), publicationId = b.PublicationId.ToString("D"),
            baseCheckId = b.BaseCheckId.ToString("D"), requestedReviewerId = actor.SubjectId.ToString("D"), baseOutcome = b.Outcome.ToString(),
            submittedAt = AppealStorage.Stamp(before.SubmittedAt), acceptedAt = AppealStorage.Stamp(now) };
        var message = Guid.NewGuid();
        var envelope = JsonSerializer.Serialize(new { messageId = message.ToString("D"), messageType = "matigr.appeals.recheck-requested", schemaVersion = 1,
            producer = "Appeals", occurredAt = AppealStorage.Stamp(now), aggregateType = "Appeal", aggregateId = appeal.ToString("D"),
            aggregateVersion = AppealStorage.Number(selected.Version), correlationId = appeal.ToString("D"), causationId = mutex, payload }, AppealStorage.Json);
        AppealStorage.Update(row, selected);
        db.Attempts.Add(new() { RequestId = attempt.RequestId, AppealId = appeal, ReviewerId = actor.SubjectId, AcceptedAt = now, PayloadHash = AppealStorage.Hash(JsonSerializer.Serialize(payload, AppealStorage.Json)) });
        db.History.Add(new() { HistoryId = Guid.NewGuid(), AppealId = appeal, Version = selected.Version, State = selected.State.ToString(),
            ActorId = actor.SubjectId, RequestId = attempt.RequestId, Snapshot = row.Snapshot, OccurredAt = now });
        db.Outbox.Add(new() { MessageId = message, Envelope = envelope, RoutingKey = "appeals.recheck-requested.v1", CreatedAt = now, NextAttemptAt = now });
        var response = JsonSerializer.Serialize(new { appealId = appeal.ToString("D"), state = selected.State.ToString(), version = AppealStorage.Number(selected.Version) });
        var location = $"/api/v1/appeals/{appeal:D}"; var etag = $"\"{AppealStorage.Number(selected.Version)}\"";
        db.HttpReceipts.Add(new() { SubjectId = actor.SubjectId, Operation = operation, Key = storageKey, RequestHash = hash,
            StatusCode = 202, Response = response, ETag = etag, Location = location, RecordedAt = now });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(202, response, etag, location);
    }
}
