using System.Text.Json;
using Json.Schema;

namespace PowerLedger.Service.Tests;

/// <summary>
/// The report schema the Worker shares, from Contract/report-v1.schema.json in the output. Its $id registers it the first
/// time it is built, and a second build of the same $id is refused, so every test that checks a report uses this loader.
/// </summary>
internal static class ReportSchema
{
    public static readonly string Contract = Path.Combine(AppContext.BaseDirectory, "Contract");

    private static readonly Lazy<JsonSchema> Shared =
        new(() => JsonSchema.FromFile(Path.Combine(Contract, "report-v1.schema.json")));

    public static EvaluationResults Evaluate(JsonElement json) =>
        Shared.Value.Evaluate(json, new EvaluationOptions { OutputFormat = OutputFormat.List });

    /// <summary>Every problem the results hold, with where it is.</summary>
    public static string Problems(EvaluationResults results) =>
        string.Join("; ", (results.Details ?? []).Where(detail => detail.Errors is { Count: > 0 })
            .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}")));

    /// <summary>Null when the JSON passes the schema; otherwise every problem, with where it is.</summary>
    public static string? Problems(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var results = Evaluate(document.RootElement);
        return results.IsValid ? null : Problems(results);
    }
}
