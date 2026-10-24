using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Matigr.Appeals.Domain;

namespace Matigr.Appeals.Infrastructure;

internal static class AppealStorage
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    internal static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    internal static Appeal Read(AppealRow row) => JsonSerializer.Deserialize<Appeal>(row.Snapshot, Json) ?? throw new InvalidOperationException("Invalid stored Appeal.");
    internal static string View(Appeal a)
    {
        var b = a.Basis;
        var view = new Dictionary<string, object> { ["appealId"] = a.AppealId.ToString("D"), ["tournamentId"] = b.TournamentId.ToString("D"),
            ["teamId"] = b.TeamId.ToString("D"), ["taskId"] = b.TaskId.ToString("D"), ["state"] = a.State.ToString(), ["version"] = Number(a.Version),
            ["resultId"] = b.ResultId.ToString("D"), ["resultVersion"] = Number(b.ResultVersion), ["baseCheckId"] = b.BaseCheckId.ToString("D"),
            ["basePublicationId"] = b.PublicationId.ToString("D"), ["baseOutcome"] = b.Outcome.ToString(), ["submittedAt"] = Stamp(a.SubmittedAt) };
        if (a.Attempt is { } attempt) view["recheckRequestId"] = attempt.RequestId.ToString("D");
        if ((a.Progress?.CheckId ?? a.Receipt?.EvaluationId ?? a.Rejection?.EvaluationId) is { } check) view["checkId"] = check.ToString("D");
        if (a.Receipt is { } receipt) view["finalPublicationId"] = receipt.PublicationId.ToString("D");
        if (a.State == AppealState.AwaitingSeniorReview) view["reviewState"] = a.Progress?.AssignmentId is null ? "SeniorQueued" : "SeniorAssigned";
        return JsonSerializer.Serialize(view, Json);
    }
    internal static void Update(AppealRow row, Appeal a)
    {
        row.State = a.State.ToString(); row.Version = a.Version; row.Snapshot = JsonSerializer.Serialize(a, Json);
        row.CurrentRequestId = a.Attempt?.RequestId; row.CheckId = a.Progress?.CheckId ?? a.Receipt?.EvaluationId ?? a.Rejection?.EvaluationId;
        row.FinalPublicationId = a.Receipt?.PublicationId;
    }
}
