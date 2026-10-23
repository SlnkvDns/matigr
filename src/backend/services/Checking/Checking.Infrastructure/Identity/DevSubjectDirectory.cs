using System.Security.Cryptography;
using System.Text.Json;
using Matigr.Checking.Application.Identity;

namespace Matigr.Checking.Infrastructure.Identity;

// Only constructed by the Local/Test adapter. No accounts or registration storage.
public sealed class DevSubjectDirectory
{
    private readonly Dictionary<string, AuthenticatedSubject> subjects = new(StringComparer.Ordinal);

    public DevSubjectDirectory(string directory)
    {
        var bytes = File.ReadAllBytes(Path.Combine(directory, "manifest.json"));
        var expectedHash = File.ReadAllText(Path.Combine(directory, "manifest.sha256")).Trim();
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), expectedHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Fixture checksum mismatch.");
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        var tournamentId = root.GetProperty("tournament").GetProperty("tournamentId").GetString()!;
        var teamIds = root.GetProperty("teams").EnumerateArray()
            .Select(team => team.GetProperty("teamId").GetString()!).ToHashSet(StringComparer.Ordinal);
        var subjectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("subjects").EnumerateArray())
        {
            var roles = Strings(item, "roles");
            var tournaments = Strings(item, "tournamentIds");
            var subjectId = item.GetProperty("subjectId").GetString()!;
            var key = item.GetProperty("testSubjectId").GetString()!;
            var teamId = item.TryGetProperty("teamId", out var team) ? team.GetString() : null;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(subjectId) || !subjectIds.Add(subjectId)
                || roles.Length == 0 || roles.Distinct(StringComparer.Ordinal).Count() != roles.Length
                || roles.Any(role => role is not ("Jury" or "Senior" or "Team" or "Organizer"))
                || tournaments.Length != 1 || tournaments[0] != tournamentId
                || (roles.Contains("Team") ? teamId is null || !teamIds.Contains(teamId) : teamId is not null))
                throw new InvalidOperationException("Invalid fixture identity or scope.");
            subjects.Add(key, new AuthenticatedSubject(subjectId, item.GetProperty("displayName").GetString()!,
                roles, tournaments, teamId));
        }
    }

    public AuthenticatedSubject? Find(string testSubjectId) => subjects.GetValueOrDefault(testSubjectId);

    private static string[] Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(value => value.GetString()!).ToArray();
}
