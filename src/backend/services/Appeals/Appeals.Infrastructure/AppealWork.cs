using System.Text.Json;
using Matigr.Appeals.Application;
using Matigr.Appeals.Domain;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Appeals.Infrastructure;

internal sealed class AppealWork(AppealsDbContext db) : IAppealWork
{
    private const int QueuePageSize = 50;
    private sealed record QueueCursor(Guid TournamentId, DateTimeOffset SubmittedAt, Guid AppealId);

    private static bool Jury(AppealActor actor, Guid tournament) => actor.Has("Jury", tournament) || actor.Has("Senior", tournament);
    private static bool Visible(AppealActor actor, AppealRow row) => Jury(actor, row.TournamentId)
        || actor.Has("Team", row.TournamentId) && actor.TeamId == row.TeamId;
    public async Task<AppealReply> ListAsync(AppealActor actor, Guid tournament, bool own, CancellationToken ct, string? cursor = null)
    {
        if (own ? !actor.Has("Team", tournament) || actor.TeamId is null : !Jury(actor, tournament)) throw new AppealOperationException(403, "Forbidden");
        var source = db.Appeals.AsQueryable();
        if (cursor is not null)
        {
            if (own) throw new AppealOperationException(400, "InvalidRequest");
            var position = ReadCursor(cursor, tournament);
            source = db.Appeals.FromSqlInterpolated($"""
                SELECT * FROM appeals WHERE tournament_id = {tournament} AND state = 'Registered'
                    AND (submitted_at, appeal_id) > ({position.SubmittedAt}, {position.AppealId})
                """);
        }
        var query = source.AsNoTracking().Where(x => x.TournamentId == tournament);
        query = own ? query.Where(x => x.TeamId == actor.TeamId) : query.Where(x => x.State == "Registered");
        query = query.OrderBy(x => x.SubmittedAt).ThenBy(x => x.AppealId);
        var rows = await (own ? query : query.Take(QueuePageSize + 1)).ToListAsync(ct);
        var page = own ? rows : rows.Take(QueuePageSize);
        var items = page.Select(x => new {
            appealId = x.AppealId.ToString("D"), tournamentId = x.TournamentId.ToString("D"), teamId = x.TeamId.ToString("D"), taskId = x.TaskId.ToString("D"),
            state = x.State, submittedAt = AppealStorage.Stamp(x.SubmittedAt), version = AppealStorage.Number(x.Version) });
        var data = new { tournamentId = tournament.ToString("D"), items };
        if (own || rows.Count <= QueuePageSize) return new(200, JsonSerializer.Serialize(data, AppealStorage.Json));
        var last = rows[QueuePageSize - 1];
        var nextCursor = System.Buffers.Text.Base64Url.EncodeToString(
            JsonSerializer.SerializeToUtf8Bytes(new QueueCursor(tournament, last.SubmittedAt, last.AppealId), AppealStorage.Json));
        return new(200, JsonSerializer.Serialize(new { data.tournamentId, items, nextCursor }, AppealStorage.Json));
    }

    private static QueueCursor ReadCursor(string cursor, Guid tournament)
    {
        try
        {
            if (cursor.Length <= 256 && JsonSerializer.Deserialize<QueueCursor>(
                System.Buffers.Text.Base64Url.DecodeFromChars(cursor), AppealStorage.Json) is { } position
                && position.TournamentId == tournament && position.AppealId != Guid.Empty
                && position.SubmittedAt > DateTimeOffset.MinValue && position.SubmittedAt.Offset == TimeSpan.Zero)
                return position;
        }
        catch (Exception error) when (error is FormatException or JsonException) { }
        throw new AppealOperationException(400, "InvalidRequest");
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
        var attempt = selected.Attempt!;
        var (message, payloadHash) = RecheckRequestedEvent.Create(selected, mutex);
        AppealStorage.Update(row, selected);
        db.Attempts.Add(new() { RequestId = attempt.RequestId, AppealId = appeal, ReviewerId = actor.SubjectId, AcceptedAt = now, PayloadHash = payloadHash });
        db.History.Add(new() { HistoryId = Guid.NewGuid(), AppealId = appeal, Version = selected.Version, State = selected.State.ToString(),
            ActorId = actor.SubjectId, RequestId = attempt.RequestId, Snapshot = row.Snapshot, OccurredAt = now });
        db.Outbox.Add(message);
        var response = JsonSerializer.Serialize(new { appealId = appeal.ToString("D"), state = selected.State.ToString(), version = AppealStorage.Number(selected.Version) });
        var location = $"/api/v1/appeals/{appeal:D}"; var etag = $"\"{AppealStorage.Number(selected.Version)}\"";
        db.HttpReceipts.Add(new() { SubjectId = actor.SubjectId, Operation = operation, Key = storageKey, RequestHash = hash,
            StatusCode = 202, Response = response, ETag = etag, Location = location, RecordedAt = now });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(202, response, etag, location);
    }
}
