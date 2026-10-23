using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Matigr.Transport;

// The published schemas use this closed subset of Draft 2020-12. Unsupported assertion keywords fail startup.
// Schemas remain producer/consumer-owned embedded resources; this code contains no business schema.
public sealed class WireSchema
{
    private readonly JsonDocument document;
    private static readonly HashSet<string> Supported = ["$schema", "$id", "$defs", "$ref", "title", "description", "type",
        "properties", "required", "additionalProperties", "propertyNames", "const", "enum", "pattern", "format", "minLength",
        "minimum", "maximum", "items", "allOf", "anyOf", "not", "if", "then", "else", "examples", "default"];
    public WireSchema(Assembly assembly, string resource)
    {
        var name = assembly.GetManifestResourceNames().Single(x => x.EndsWith(resource, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        document = JsonDocument.Parse(stream); Inspect(document.RootElement);
    }
    private static void Inspect(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        foreach (var p in schema.EnumerateObject())
        {
            if (!Supported.Contains(p.Name)) throw new InvalidOperationException("Unsupported schema assertion: " + p.Name);
            if (p.Name == "$ref" && !p.Value.GetString()!.StartsWith("#/", StringComparison.Ordinal)) throw new InvalidOperationException("External schema references are not configured.");
            if (p.Name == "format" && p.Value.GetString() != "date-time") throw new InvalidOperationException("Unsupported schema format.");
            if (p.Name is "properties" or "$defs") foreach (var child in p.Value.EnumerateObject()) Inspect(child.Value);
            if (p.Name is "allOf" or "anyOf") foreach (var child in p.Value.EnumerateArray()) Inspect(child);
            if (p.Name is "items" or "not" or "if" or "then" or "else" or "propertyNames" or "additionalProperties") Inspect(p.Value);
        }
    }
    public void Validate(JsonElement value)
    { if (!Valid(document.RootElement, value)) throw new InvalidMessageException("InvalidSchema"); }
    private bool Valid(JsonElement schema, JsonElement value)
    {
        if (schema.ValueKind == JsonValueKind.True) return true;
        if (schema.ValueKind == JsonValueKind.False) return false;
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var path = reference.GetString()!;
            if (!path.StartsWith("#/", StringComparison.Ordinal)) throw new InvalidOperationException("External schema references are not configured.");
            var target = document.RootElement;
            foreach (var part in path[2..].Split('/')) target = target.GetProperty(part.Replace("~1", "/").Replace("~0", "~"));
            if (!Valid(target, value)) return false;
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var matches = type.GetString() switch {
                "object" => value.ValueKind == JsonValueKind.Object, "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String, "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number == decimal.Truncate(number),
                _ => throw new InvalidOperationException("Unsupported schema type.") };
            if (!matches) return false;
        }
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value)) return false;
        if (schema.TryGetProperty("enum", out var enumeration) && !enumeration.EnumerateArray().Any(x => JsonElement.DeepEquals(x, value))) return false;
        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString()!;
            if (schema.TryGetProperty("minLength", out var length) && s.EnumerateRunes().Count() < length.GetInt32()) return false;
            if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(s, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) return false;
            if (schema.TryGetProperty("format", out var format) && format.GetString() == "date-time"
                && (!Regex.IsMatch(s, @"\A\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:[Zz]|[+-]\d{2}:\d{2})\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                    || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))) return false;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n))
        {
            if (schema.TryGetProperty("minimum", out var min) && n < min.GetDecimal()) return false;
            if (schema.TryGetProperty("maximum", out var max) && n > max.GetDecimal()) return false;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count()) return false;
            if (schema.TryGetProperty("required", out var required) && required.EnumerateArray().Any(x => !value.TryGetProperty(x.GetString()!, out _))) return false;
            var hasProperties = schema.TryGetProperty("properties", out var properties);
            foreach (var p in value.EnumerateObject())
            {
                if (schema.TryGetProperty("propertyNames", out var names) && !Valid(names, JsonSerializer.SerializeToElement(p.Name))) return false;
                if (hasProperties && properties.TryGetProperty(p.Name, out var definition)) { if (!Valid(definition, p.Value)) return false; }
                else if (schema.TryGetProperty("additionalProperties", out var additional) && !Valid(additional, p.Value)) return false;
            }
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var item) && value.EnumerateArray().Any(x => !Valid(item, x))) return false;
        if (schema.TryGetProperty("allOf", out var all) && all.EnumerateArray().Any(x => !Valid(x, value))) return false;
        if (schema.TryGetProperty("anyOf", out var any) && !any.EnumerateArray().Any(x => Valid(x, value))) return false;
        if (schema.TryGetProperty("not", out var not) && Valid(not, value)) return false;
        if (schema.TryGetProperty("if", out var condition))
        {
            var branch = Valid(condition, value) ? "then" : "else";
            if (schema.TryGetProperty(branch, out var action) && !Valid(action, value)) return false;
        }
        return true;
    }
}
