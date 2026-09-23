using System.Reflection;
using System.Text.Json;
using Json.Schema;

namespace PowerLedger.Service.Tests;

/// <summary>
/// The report schema as <see cref="SharingContractTests"/> loads it. Its $id registers it the first time it is built, and a
/// second build of the same $id is refused, so everything that checks a report shares that one loader.
/// </summary>
internal static class ReportSchema
{
    private static readonly Lazy<JsonSchema> Shared = (Lazy<JsonSchema>)typeof(SharingContractTests)
        .GetField("Schema", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    /// <summary>Null when the JSON passes the schema; otherwise every problem, with where it is.</summary>
    public static string? Problems(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var results = Shared.Value.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid) return null;
        return string.Join("; ", (results.Details ?? []).Where(detail => detail.Errors is { Count: > 0 })
            .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}")));
    }
}
