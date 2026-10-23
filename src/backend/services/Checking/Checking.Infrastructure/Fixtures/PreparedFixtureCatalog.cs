using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matigr.Checking.Domain;

namespace Matigr.Checking.Infrastructure.Fixtures;

internal sealed record PreparedSolution(SolutionReference Snapshot, string RelativeFile, string Sha256);

internal sealed class PreparedFixtureCatalog
{
    public string DirectoryPath { get; }
    public long Version { get; }
    public string ManifestHash { get; }
    public IReadOnlyList<PreparedSolution> Solutions { get; }
    public IReadOnlyList<ReviewerScope> ReviewerScopes { get; }

    public PreparedFixtureCatalog(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        var bytes = File.ReadAllBytes(Path.Combine(DirectoryPath, "manifest.json"));
        ManifestHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (ManifestHash != File.ReadAllText(Path.Combine(DirectoryPath, "manifest.sha256")).Trim())
            throw new InvalidOperationException("Prepared fixture manifest checksum mismatch.");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Version = PositiveVersion(root.GetProperty("fixtureVersion"));
        var tournament = Id(root.GetProperty("tournament"), "tournamentId");
        var teams = root.GetProperty("teams").EnumerateArray().Select(item => Id(item, "teamId")).ToHashSet();
        var tasks = root.GetProperty("tasks").EnumerateArray().Select(item => Id(item, "taskId")).ToHashSet();
        var solutions = new List<PreparedSolution>();
        foreach (var item in root.GetProperty("solutions").EnumerateArray())
        {
            var snapshot = new SolutionReference(Id(item, "solutionId"), Id(item, "tournamentId"), Id(item, "teamId"),
                Id(item, "taskId"), Id(item, "materialVersionId"), item.GetProperty("materialRef").GetString()!,
                DateTimeOffset.ParseExact(item.GetProperty("acceptedAt").GetString()!, "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal), PositiveVersion(item.GetProperty("sourceVersion")));
            var file = item.GetProperty("materialFile").GetString()!;
            var sha = item.GetProperty("materialSha256").GetString()!;
            if (snapshot.TournamentId != tournament || !teams.Contains(snapshot.TeamId) || !tasks.Contains(snapshot.TaskId)
                || !Regex.IsMatch(file, @"\Amaterials/[a-z0-9-]+\.txt\z", RegexOptions.CultureInvariant)
                || !Regex.IsMatch(sha, @"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("Invalid prepared solution reference or material allowlist.");
            solutions.Add(new(snapshot, file, sha));
        }
        if (solutions.Select(x => x.Snapshot.SolutionId).Distinct().Count() != solutions.Count
            || solutions.Select(x => (x.Snapshot.TournamentId, x.Snapshot.TeamId, x.Snapshot.TaskId)).Distinct().Count() != solutions.Count
            || solutions.Select(x => x.Snapshot.MaterialVersionId).Distinct().Count() != solutions.Count
            || solutions.Select(x => x.Snapshot.MaterialRef).Distinct(StringComparer.Ordinal).Count() != solutions.Count
            || solutions.Select(x => x.RelativeFile).Distinct(StringComparer.Ordinal).Count() != solutions.Count)
            throw new InvalidOperationException("Duplicate prepared solution or material identity.");
        Solutions = solutions.AsReadOnly();
        var scopes = new List<ReviewerScope>();
        foreach (var item in root.GetProperty("subjects").EnumerateArray())
        {
            var reviewer = Id(item, "subjectId");
            foreach (var scope in item.GetProperty("tournamentIds").EnumerateArray())
            {
                var tournamentId = Guid.Parse(scope.GetString()!);
                if (tournamentId != tournament) throw new InvalidOperationException("Invalid fixture reviewer tournament.");
                foreach (var role in item.GetProperty("roles").EnumerateArray())
                    if (role.GetString() is "Jury" or "Senior")
                        scopes.Add(new ReviewerScope(reviewer, tournamentId, role.GetString() == "Jury" ? ReviewerRole.Reviewer : ReviewerRole.Senior));
            }
        }
        if (scopes.Distinct().Count() != scopes.Count) throw new InvalidOperationException("Duplicate fixture reviewer scope.");
        ReviewerScopes = scopes.AsReadOnly();
    }

    private static Guid Id(JsonElement item, string name)
    {
        var id = item.GetProperty(name).GetGuid();
        return id != Guid.Empty ? id : throw new InvalidOperationException("Empty fixture identity.");
    }

    private static long PositiveVersion(JsonElement value)
    {
        var text = value.GetString()!;
        if (!Regex.IsMatch(text, @"\A[1-9][0-9]*\z", RegexOptions.CultureInvariant)
            || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            throw new InvalidOperationException("Invalid fixture version.");
        return version;
    }
}
