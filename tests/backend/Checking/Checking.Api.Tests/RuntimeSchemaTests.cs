using System.Text.Json;
using System.Text.Json.Nodes;
using Matigr.Checking.Infrastructure;
using Matigr.Transport;

namespace Matigr.Checking.Api.Tests;

public class RuntimeSchemaTests
{
    [Theory]
    [InlineData("check-completed.initial", "check-completed", "Results")]
    [InlineData("check-completed.initial-senior", "check-completed", "Results")]
    [InlineData("check-completed.appeal", "check-completed", "Appeals")]
    [InlineData("check-completed.appeal-senior", "check-completed", "Appeals")]
    [InlineData("recheck-assigned.selected", "recheck-assigned", "Appeals")]
    [InlineData("recheck-assigned.senior", "recheck-assigned", "Appeals")]
    [InlineData("recheck-escalated", "recheck-escalated", "Appeals")]
    [InlineData("recheck-request-rejected.busy", "recheck-request-rejected", "Appeals")]
    public void Runtime_producer_and_independent_consumer_agree_and_reject_unknown_version(string fixture, string message, string consumer)
    {
        var body = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WireFixtures", fixture + ".json")))!;
        var producer = new WireSchema(typeof(CheckingTransport).Assembly, message + ".v1.schema.json");
        var expectation = new WireSchema(typeof(RuntimeSchemaTests).Assembly, "Consumer." + consumer + "." + message + ".v1.schema.json");
        void Both() { var json = JsonSerializer.SerializeToElement(body); producer.Validate(json); expectation.Validate(json); }
        Both(); body["schemaVersion"] = 2;
        Assert.Throws<InvalidMessageException>(() => producer.Validate(JsonSerializer.SerializeToElement(body)));
        Assert.Throws<InvalidMessageException>(() => expectation.Validate(JsonSerializer.SerializeToElement(body)));
    }
    [Fact]
    public void Runtime_rejects_missing_escalation_wrong_selected_lineage_and_private_fields()
    {
        JsonNode Read(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WireFixtures", name + ".json")))!;
        var assembly = typeof(CheckingTransport).Assembly;
        var schema = new WireSchema(assembly, "check-completed.v1.schema.json");
        var senior = Read("check-completed.appeal-senior"); senior["payload"]!.AsObject().Remove("escalationId");
        Assert.Throws<InvalidMessageException>(() => schema.Validate(JsonSerializer.SerializeToElement(senior)));
        var privateField = Read("check-completed.initial"); privateField["payload"]!["capability"] = "must-not-cross";
        Assert.Throws<InvalidMessageException>(() => schema.Validate(JsonSerializer.SerializeToElement(privateField)));
        var invalidDate = Read("check-completed.initial"); invalidDate["occurredAt"] = "2026-10-23Z";
        Assert.Throws<InvalidMessageException>(() => schema.Validate(JsonSerializer.SerializeToElement(invalidDate)));
        var selected = Read("recheck-assigned.selected"); selected["payload"]!["reviewerId"] = "different";
        var contracts = new TransportSchemas(new Dictionary<string, WireSchema>(), new Dictionary<string, WireSchema> { ["route"] = new(assembly, "recheck-assigned.v1.schema.json") });
        Assert.Throws<InvalidMessageException>(() => contracts.Validate(JsonSerializer.SerializeToElement(selected), "route", true));
        var rename = Read("check-completed.appeal"); rename["payload"]!["requestId"] = rename["payload"]!["recheckRequestId"]!.GetValue<string>();
        rename["payload"]!.AsObject().Remove("recheckRequestId");
        Assert.Throws<InvalidMessageException>(() => schema.Validate(JsonSerializer.SerializeToElement(rename)));
    }
}
