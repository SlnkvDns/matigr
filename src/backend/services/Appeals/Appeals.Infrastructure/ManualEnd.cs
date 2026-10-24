using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matigr.Appeals.Application;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Appeals.Infrastructure;

internal sealed class ManualEnd(AppealsDbContext db) : IManualEnd
{
    public async Task<AppealReply> RecordAsync(AppealActor actor, Guid tournament, Guid fact, string key, CancellationToken ct)
    {
        if (!actor.Has("Organizer", tournament)) throw new AppealOperationException(403, "Forbidden");
        if (tournament == Guid.Empty || fact == Guid.Empty || string.IsNullOrWhiteSpace(key)) throw new AppealOperationException(400, "InvalidRequest");
        string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        var hash = Hash($"{tournament:D}/{fact:D}"); var storageKey = Hash(key); const string operation = "ManualEnd";
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var mutex = $"{actor.SubjectId:D}/{operation}/{storageKey}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({mutex},0))", ct);
        var previous = await db.HttpReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.SubjectId == actor.SubjectId && x.Operation == operation && x.Key == storageKey, ct);
        if (previous is not null) {
            if (previous.RequestHash != hash) throw new AppealOperationException(409, "IdempotencyConflict");
            return new(previous.StatusCode, previous.Response, previous.ETag, previous.Location);
        }
        var factMutex = "end/" + fact.ToString("D");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({factMutex},0))", ct);
        var oldFact = await db.EndFacts.AsNoTracking().SingleOrDefaultAsync(x => x.FactId == fact, ct);
        if (oldFact is not null && (oldFact.TournamentId != tournament || oldFact.ActorId != actor.SubjectId || oldFact.PayloadHash != hash))
            throw new AppealOperationException(409, "EndFactConflict");
        var window = (await db.SubmissionWindows.FromSqlInterpolated($"SELECT * FROM submission_windows WHERE tournament_id={tournament} FOR UPDATE").ToListAsync(ct)).SingleOrDefault()
            ?? throw new AppealOperationException(404, "TournamentNotFound");
        var now = DateTimeOffset.UtcNow;
        if (window.State == "Open") {
            window.State = "Closed"; window.Version = checked(window.Version + 1); window.EndFactId = fact;
            window.EndedBy = actor.SubjectId; window.EndedAt = now;
        }
        var json = oldFact?.Response ?? JsonSerializer.Serialize(new { tournamentId = tournament.ToString("D"), state = "Closed",
            version = window.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), factId = window.EndFactId!.Value.ToString("D") });
        var etag = $"\"{window.Version}\"";
        if (oldFact is null) db.EndFacts.Add(new() { FactId = fact, TournamentId = tournament, ActorId = actor.SubjectId, PayloadHash = hash, Response = json, RecordedAt = now });
        db.HttpReceipts.Add(new() { SubjectId = actor.SubjectId, Operation = operation, Key = storageKey, RequestHash = hash,
            StatusCode = 200, Response = json, ETag = etag, RecordedAt = now });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(200, json, etag);
    }
}
