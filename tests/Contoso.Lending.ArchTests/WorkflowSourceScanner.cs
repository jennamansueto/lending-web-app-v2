using System.Text;
using System.Text.RegularExpressions;

namespace Contoso.Lending.ArchTests;

internal sealed record Violation(string Rule, string File, int Line, string Snippet, string Explanation)
{
    public override string ToString() =>
        $"{File}:{Line}: {Rule} — {Explanation}{Environment.NewLine}    {Snippet.Trim()}";
}

/// <summary>
/// Reads the layer-3 sources and reports business logic hiding in them. Layer 3 sequences the
/// service and stores what it returned; any calculation, threshold or rounding here belongs in
/// layer 2 (docs/architecture.md).
/// </summary>
internal static class WorkflowSourceScanner
{
    /// <summary>Identifier fragments that mark a money, rate or score value.</summary>
    private const string MoneyWords =
        "amount|rate|fee|payment|principal|interest|balance|dti|ltv|income|debt|collateral|deposit|score|spread|discount|adjustment|payoff|quote";

    private static readonly RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex MoneyIdentifier = new($@"\b[\w\.]*(?:{MoneyWords})[\w]*\b", Options);

    // decimal arithmetic on a money/rate value: `amount * 0.01m`, `fee + surcharge`, `rate - discount`
    private static readonly Regex MoneyArithmetic = new(
        $@"(\b[\w\.]*(?:{MoneyWords})[\w]*\b\s*[-+*/%]\s*[\w\.\(]|[-+*/%]\s*\b[\w\.]*(?:{MoneyWords})[\w]*\b)", Options);

    // `0.01m`, `25000m` — money constants
    private static readonly Regex DecimalLiteral = new(@"(?<![\w.])\d[\d_]*(\.\d+)?[mM](?![\w])", Options);

    // any fractional constant: a rate, a cap or a ratio by definition
    private static readonly Regex FractionalLiteral = new(@"(?<![\w.])\d[\d_]*\.\d+[mMdDfF]?(?![\w])", Options);

    // `creditScore < 640`, `ltv >= 0.9` — a threshold that decides an outcome
    private static readonly Regex Comparison = new(@"([\w\.]+)\s*(<=|>=|<|>)\s*([\w\.]+)", Options);

    private static readonly Regex Rounding = new(@"\b(Math\.|decimal\.(Round|Add|Subtract|Multiply|Divide)|MidpointRounding)", Options);

    private static readonly Regex ForbiddenReference = new(
        @"\b(Contoso\.Lending\.Domain|Npgsql[\w\.]*|Oracle\.[\w\.]+|System\.Data\.(SqlClient|Odbc|OleDb|Common)|Microsoft\.Data\.[\w\.]+|EntityFrameworkCore|Dapper)\b",
        RegexOptions.CultureInvariant);

    internal static IReadOnlyList<Violation> Scan(string directory)
    {
        var violations = new List<Violation>();
        var files = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(path => path, StringComparer.Ordinal);

        foreach (string path in files)
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                violations.AddRange(ScanLine(Path.GetFileName(path), i + 1, lines[i]));
            }
        }
        return violations;
    }

    internal static IReadOnlyList<Violation> ScanLine(string file, int lineNumber, string rawLine)
    {
        string line = StripCommentsAndStrings(rawLine);
        if (line.Trim().Length == 0)
        {
            return Array.Empty<Violation>();
        }

        var violations = new List<Violation>();
        void Add(string rule, string explanation) =>
            violations.Add(new Violation(rule, file, lineNumber, rawLine, explanation));

        if (MoneyArithmetic.IsMatch(line))
        {
            Add("money-arithmetic", "arithmetic on a money, rate or score value — calculations belong to layer 2");
        }
        if (DecimalLiteral.IsMatch(line))
        {
            Add("decimal-literal", "a decimal money constant — layer 3 stores the numbers the service returns");
        }
        if (FractionalLiteral.IsMatch(line))
        {
            Add("fractional-literal", "a fractional constant is a rate, ratio or cap — it belongs to layer 2");
        }
        if (Rounding.IsMatch(line))
        {
            Add("rounding", "rounding or Math.* — legacy rounding behavior is a layer-2 rule");
        }
        if (ForbiddenReference.IsMatch(line))
        {
            Add("forbidden-reference", "a domain or database type — layer 3 reaches the service over HTTP only");
        }
        foreach (Match match in Comparison.Matches(line))
        {
            string left = match.Groups[1].Value;
            string op = match.Groups[2].Value;
            string right = match.Groups[3].Value;
            bool money = MoneyIdentifier.IsMatch(left) || MoneyIdentifier.IsMatch(right);
            bool numeric = IsNumeric(left) || IsNumeric(right);
            // `<`/`>` alone are ambiguous with generics, so a bare threshold must pair a money
            // value with a literal; `<=`/`>=` are never generics.
            bool threshold = money && (numeric || op.Length == 2);
            if (threshold)
            {
                Add("threshold-comparison",
                    $"'{match.Value.Trim()}' compares a money, rate or score value — thresholds belong to layer 2");
            }
        }
        return violations;
    }

    private static bool IsNumeric(string token) =>
        token.Length > 0 && (char.IsDigit(token[0]) || (token[0] == '.' && token.Length > 1 && char.IsDigit(token[1])));

    /// <summary>Drops `//` comments and string/char literals so prose and URLs never trip a rule.</summary>
    private static string StripCommentsAndStrings(string line)
    {
        var builder = new StringBuilder(line.Length);
        bool inString = false;
        bool inChar = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (!inString && !inChar && c == '/' && i + 1 < line.Length && (line[i + 1] == '/' || line[i + 1] == '*'))
            {
                break;
            }
            if (!inChar && c == '"' && (i == 0 || line[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }
            if (!inString && c == '\'' && (i == 0 || line[i - 1] != '\\'))
            {
                inChar = !inChar;
                continue;
            }
            builder.Append(inString || inChar ? ' ' : c);
        }
        string stripped = builder.ToString();
        return stripped.TrimStart().StartsWith('*') ? string.Empty : stripped;
    }
}
