namespace Contoso.Lending.Domain;

public sealed class PrequalificationResult
{
    public required string PrequalifiedProducts { get; init; }
    public required string LabelText { get; init; }
}

/// <summary>
/// BR-PQL-001 BorrowerLookupForm.cs:54-76 (grid_SelectionChanged), bands at :65-68.
/// LEND-3987: these thresholds are an independent, manually-maintained copy of the
/// eligibility minimums in BR-ELG-004 — they are NOT derived from them and must
/// stay duplicated here, drift and all. Do not unify.
/// </summary>
public static class PrequalificationEngine
{
    public static PrequalificationResult Evaluate(int creditScore)
    {
        int score = creditScore;
        string prequal;
        if (score >= 660) prequal = "TERM, LOC, EQUIP";
        else if (score >= 640) prequal = "TERM, EQUIP";
        else if (score >= 620) prequal = "EQUIP only";
        else prequal = "None \u2014 refer to special assets";

        return new PrequalificationResult
        {
            PrequalifiedProducts = prequal,
            // BR-UI-008 BorrowerLookupForm.cs:70 — label prefix is byte-exact.
            LabelText = "Pre-qualified products: " + prequal,
        };
    }
}
