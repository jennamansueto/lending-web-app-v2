namespace Contoso.Lending.Domain;

/// <summary>
/// Pre-qualification hint extracted 1:1 from legacy
/// `src/LendingDesk/Forms/BorrowerLookupForm.grid_SelectionChanged`.
/// </summary>
public static class PreQualificationEngine
{
    // BR-PQL-001: pre-qualification bands.
    // Legacy quirk (LEND-3987): these thresholds are a manual copy of the loan application
    // screen's minimum credit scores (640/660/620) and drifted into their own rule — the
    // bands below are NOT derived from BR-ELG-004, they are the duplicated 2016 copy and are
    // reproduced as a separate rule exactly as the legacy screen had them.
    public static string PrequalifiedProducts(int creditScore)
    {
        if (creditScore >= 660) return "TERM, LOC, EQUIP";
        if (creditScore >= 640) return "TERM, EQUIP";
        if (creditScore >= 620) return "EQUIP only";
        return "None \u2014 refer to special assets";
    }

    // BR-UI-006: the exact label text the lookup screen renders.
    public static string LabelText(int creditScore) =>
        "Pre-qualified products: " + PrequalifiedProducts(creditScore);

    public static PreQualificationResult Evaluate(int creditScore) =>
        new(creditScore, PrequalifiedProducts(creditScore), LabelText(creditScore));
}

public sealed record PreQualificationResult(int CreditScore, string PrequalifiedProducts, string ResultText);
