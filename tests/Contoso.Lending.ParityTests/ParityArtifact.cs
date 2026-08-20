using System.Collections.Concurrent;
using System.Text.Json;

namespace Contoso.Lending.ParityTests;

internal static class ParityArtifact
{
    private static readonly ConcurrentDictionary<(string File, int Index), CaseResult> Cases = new();
    private static readonly object FlushLock = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
    };

    public static void Reset() => Cases.Clear();

    public static void Begin(
        string file,
        int index,
        JsonElement input,
        JsonElement expected,
        string? title)
    {
        Cases[(file, index)] = new CaseResult(
            file,
            index,
            title,
            input.Clone(),
            expected.Clone(),
            null,
            null);
        Flush(DefaultPath());
    }

    public static void Complete(string file, int index, object actual)
    {
        Complete(file, index, JsonSerializer.SerializeToElement(actual, Json));
    }

    public static void Complete(string file, int index, JsonElement actual)
    {
        var key = (file, index);
        if (!Cases.TryGetValue(key, out var result))
        {
            throw new InvalidOperationException($"Parity case was not started: {file}#{index}");
        }

        bool passed = Equivalent(result.Expected, actual);
        Cases[key] = result with
        {
            Actual = actual.Clone(),
            AssertionMessage = passed
                ? null
                : $"Assert.Equal() Failure: Expected: {result.Expected.GetRawText()} Actual: {actual.GetRawText()}",
        };
        Flush(DefaultPath());
    }

    public static void Flush(string path)
    {
        lock (FlushLock)
        {
            var records = Cases.Values
                .OrderBy(x => x.File, StringComparer.Ordinal)
                .ThenBy(x => x.Index)
                .Select(x =>
                {
                    var actual = x.Actual ?? JsonSerializer.SerializeToElement<object?>(null, Json);
                    var passed = x.Actual is not null && Equivalent(x.Expected, x.Actual.Value);
                    return new
                    {
                        ruleId = x.File.Split('_', 2)[0],
                        goldenFile = $"parity/golden/{x.File}",
                        title = x.Title,
                        recordIndex = x.Index,
                        input = x.Input,
                        expected = x.Expected,
                        actual,
                        status = passed ? "pass" : "fail",
                        assertionMessage = passed
                            ? (string?)null
                            : x.AssertionMessage ?? "Case did not complete; the test failed before recording actual values.",
                    };
                })
                .ToArray();

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(records, new JsonSerializerOptions(Json)
            {
                WriteIndented = true,
            }) + Environment.NewLine);
        }
    }

    private static string DefaultPath()
    {
        var root = Directory.GetParent(GoldenCorpus.Dir)!.Parent!.FullName;
        return Environment.GetEnvironmentVariable("PARITY_L2_ARTIFACT")
            ?? Path.Combine(root, "parity", "artifacts", "l2-cases.json");
    }

    private static bool Equivalent(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            if (expected.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.Number)
            {
                return expected.GetDecimal() == actual.GetDecimal();
            }

            return false;
        }

        return expected.ValueKind switch
        {
            JsonValueKind.Object => ObjectsEqual(expected, actual),
            JsonValueKind.Array => expected.EnumerateArray().Zip(actual.EnumerateArray())
                .All(pair => Equivalent(pair.First, pair.Second))
                && expected.GetArrayLength() == actual.GetArrayLength(),
            JsonValueKind.Number => expected.GetDecimal() == actual.GetDecimal(),
            JsonValueKind.String => expected.GetString() == actual.GetString(),
            JsonValueKind.True or JsonValueKind.False => expected.GetBoolean() == actual.GetBoolean(),
            JsonValueKind.Null => true,
            _ => expected.GetRawText() == actual.GetRawText(),
        };
    }

    private static bool ObjectsEqual(JsonElement expected, JsonElement actual)
    {
        var expectedProperties = expected.EnumerateObject().ToDictionary(x => x.Name, x => x.Value);
        var actualProperties = actual.EnumerateObject().ToDictionary(x => x.Name, x => x.Value);
        return expectedProperties.Count == actualProperties.Count
            && expectedProperties.All(x =>
                actualProperties.TryGetValue(x.Key, out var value) && Equivalent(x.Value, value));
    }

    private sealed record CaseResult(
        string File,
        int Index,
        string? Title,
        JsonElement Input,
        JsonElement Expected,
        JsonElement? Actual,
        string? AssertionMessage);
}
