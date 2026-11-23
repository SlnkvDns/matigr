using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Matigr.Results.Domain;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Results.Infrastructure;

public static class ResultFixtures
{
    public static async Task SeedAsync(ResultsDbContext db, string directory, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json"), ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (hash != (await File.ReadAllTextAsync(Path.Combine(directory, "manifest.sha256"), ct)).Trim())
            throw new InvalidOperationException("Fixture checksum mismatch.");
        using var doc = JsonDocument.Parse(bytes); var root = doc.RootElement;
        var tournament = root.GetProperty("tournament").GetProperty("tournamentId").GetGuid();
        var version = long.Parse(root.GetProperty("fixtureVersion").GetString()!, CultureInfo.InvariantCulture);
        if (tournament == Guid.Empty || version < 1) throw new InvalidOperationException("Invalid fixture identity.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var mutex = tournament.ToString("D");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({mutex}, 0))", ct);
        var previous = await db.Snapshots.AsNoTracking().SingleOrDefaultAsync(x => x.TournamentId == tournament, ct);
        if (previous is not null)
        {
            if (previous.ManifestHash != hash || previous.FixtureVersion != version) throw new InvalidOperationException("Persisted fixture set changed.");
            await tx.CommitAsync(ct); return;
        }
        db.Snapshots.Add(new() { TournamentId = tournament, Version = 0, FixtureVersion = version, ManifestHash = hash, UpdatedAt = DateTimeOffset.UtcNow });
        foreach (var team in root.GetProperty("teams").EnumerateArray())
            db.Teams.Add(new() { TournamentId = tournament, TeamId = team.GetProperty("teamId").GetGuid(), Name = team.GetProperty("name").GetString()! });
        foreach (var task in root.GetProperty("tasks").EnumerateArray())
        {
            var rule = new ScoringRule(long.Parse(task.GetProperty("scoreRuleVersion").GetString()!, CultureInfo.InvariantCulture),
                decimal.Parse(task.GetProperty("points").GetString()!, CultureInfo.InvariantCulture));
            var id = task.GetProperty("taskId").GetGuid();
            db.Tasks.Add(new() { TournamentId = tournament, TaskId = id, Name = task.GetProperty("name").GetString()!, Order = task.GetProperty("order").GetInt32() });
            db.Rules.Add(new() { TournamentId = tournament, TaskId = id, RuleVersion = rule.Version, Points = rule.Points, ManifestHash = hash });
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
