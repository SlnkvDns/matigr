using System.Text.Json;
using Matigr.Appeals.Application;
using Matigr.Appeals.Domain;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Appeals.Infrastructure;

internal sealed class AppealSubmission(AppealsDbContext db, IResultsBasis results) : IAppealSubmission
{
    public async Task<AppealReply> SubmitAsync(AppealActor actor, Guid result, long expectedVersion, string key, CancellationToken ct)
    {
        if (!actor.Roles.Contains("Team") || actor.TeamId is null || actor.SubjectId == Guid.Empty) throw new AppealOperationException(403, "Forbidden");
        if (result == Guid.Empty || expectedVersion < 1 || string.IsNullOrWhiteSpace(key)) throw new AppealOperationException(400, "InvalidRequest");
        const string operation = "Submit"; var hash = AppealStorage.Hash($"{result:D}/{AppealStorage.Number(expectedVersion)}"); var storageKey = AppealStorage.Hash(key);
        async Task<AppealReply?> Replay()
        {
            var old = await db.HttpReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.SubjectId == actor.SubjectId && x.Operation == operation && x.Key == storageKey, ct);
            if (old is null) return null;
            if (old.RequestHash != hash) throw new AppealOperationException(409, "IdempotencyConflict");
            using var view = JsonDocument.Parse(old.Response);
            if (view.RootElement.GetProperty("teamId").GetGuid() != actor.TeamId || !actor.Has("Team", view.RootElement.GetProperty("tournamentId").GetGuid()))
                throw new AppealOperationException(403, "Forbidden");
            return new(old.StatusCode, old.Response, old.ETag, old.Location);
        }
        // Successful receipt is authoritative even when Results is down or the window is now closed.
        if (await Replay() is { } previous) return previous;
        var basis = await results.GetAsync(result, expectedVersion, ct); // deliberately outside the SQL transaction
        if (basis.TeamId != actor.TeamId || !actor.Has("Team", basis.TournamentId)) throw new AppealOperationException(403, "Forbidden");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var mutex = $"{actor.SubjectId:D}/{operation}/{storageKey}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({mutex},0))", ct);
        if (await Replay() is { } concurrent) return concurrent;
        var window = (await db.SubmissionWindows.FromSqlInterpolated($"SELECT * FROM submission_windows WHERE tournament_id={basis.TournamentId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault()
            ?? throw new AppealOperationException(404, "TournamentNotFound");
        var quota = await db.Appeals.AnyAsync(x => x.TournamentId == basis.TournamentId && x.TeamId == basis.TeamId && x.TaskId == basis.TaskId, ct);
        var now = DateTimeOffset.UtcNow; now = now.AddTicks(-(now.Ticks % 10));
        Appeal appeal;
        try { appeal = Appeal.Register(Guid.NewGuid(), basis, actor.TeamId.Value, new(basis.TournamentId, window.State == "Open", window.Version), now, quota); }
        catch (AppealRuleException e) { throw new AppealOperationException(e.Message == "Forbidden" ? 403 : 409, e.Message); }
        var row = new AppealRow { AppealId = appeal.AppealId, TournamentId = basis.TournamentId, TeamId = basis.TeamId, TaskId = basis.TaskId,
            ResultId = basis.ResultId, ExpectedResultVersion = basis.ResultVersion, SolutionId = basis.SolutionId, MaterialVersionId = basis.MaterialVersionId,
            BaseCheckId = basis.BaseCheckId, BasePublicationId = basis.PublicationId, WindowVersion = window.Version, SubmittedAt = now,
            Basis = JsonSerializer.Serialize(basis, AppealStorage.Json) };
        AppealStorage.Update(row, appeal); db.Appeals.Add(row);
        db.History.Add(new() { HistoryId = Guid.NewGuid(), AppealId = appeal.AppealId, Version = appeal.Version, State = appeal.State.ToString(),
            ActorId = actor.SubjectId, Snapshot = row.Snapshot, OccurredAt = now });
        var json = AppealStorage.View(appeal); var location = $"/api/v1/appeals/{appeal.AppealId:D}";
        db.HttpReceipts.Add(new() { SubjectId = actor.SubjectId, Operation = operation, Key = storageKey, RequestHash = hash, StatusCode = 201,
            Response = json, ETag = "\"1\"", Location = location, RecordedAt = now });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(201, json, "\"1\"", location);
    }
}
