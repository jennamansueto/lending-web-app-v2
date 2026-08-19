namespace Contoso.Lending.Domain;

public sealed class EligibilityInput
{
    public required string Product { get; init; }
    public decimal Amount { get; init; }
    public int TermMonths { get; init; }
    public decimal AnnualIncome { get; init; }
    public decimal MonthlyDebt { get; init; }
    public int CreditScore { get; init; }
    public decimal CollateralValue { get; init; }
    public int YearsInBusiness { get; init; }
}

public sealed class EligibilityResult
{
    public required string Decision { get; init; }
    public required string ResultText { get; init; }
    public string? DeclineReason { get; init; }
    public required string FiredRuleId { get; init; }
    public decimal? Dti { get; init; }
    public string? DtiDisplay { get; init; }
    public decimal? Ltv { get; init; }
    public string? LtvDisplay { get; init; }
    public decimal? EstPayment { get; init; }
    public string? EstPaymentDisplay { get; init; }

    // BR-UI-006 LoanApplicationForm.cs:144-147, 161-165 — result label color
    // (DarkGreen approval / Firebrick decline) is part of the observable outcome.
    public required string ResultLabelColor { get; init; }

    public IReadOnlyList<string> RuleIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 1:1 port of the eligibility decision in
/// src/LendingDesk/Forms/LoanApplicationForm.cs:69-165 (btnSubmit_Click).
/// Rules short-circuit in source order; values computed later in the method
/// (est. payment, DTI, LTV) stay null when an earlier rule declines first.
/// </summary>
public static class EligibilityEngine
{
    public static EligibilityResult Evaluate(EligibilityInput input)
    {
        var evaluated = new List<string>();
        string product = input.Product;
        decimal amount = input.Amount;
        int termMonths = input.TermMonths;
        decimal annualIncome = input.AnnualIncome;
        decimal monthlyDebt = input.MonthlyDebt;
        int creditScore = input.CreditScore;
        decimal collateral = input.CollateralValue;
        int yearsInBusiness = input.YearsInBusiness;

        // BR-ELG-001 LoanApplicationForm.cs:83-87 — strict <, boundary $25,000 passes.
        evaluated.Add("BR-ELG-001");
        if (amount < 25000m)
        {
            return Reject("Loan amount below $25,000 minimum.", "BR-ELG-001", evaluated);
        }

        // BR-ELG-002 LoanApplicationForm.cs:88-92 — strict >, boundary $5,000,000 passes.
        evaluated.Add("BR-ELG-002");
        if (amount > 5000000m)
        {
            return Reject("Loan amount exceeds $5,000,000 desk limit; refer to Credit Committee.", "BR-ELG-002", evaluated);
        }

        // BR-ELG-003 LoanApplicationForm.cs:95-100 — 12..maxTerm inclusive;
        // EQUIP 84, TERM 120, anything else (LOC) 36.
        evaluated.Add("BR-ELG-003");
        int maxTerm = product == "EQUIP" ? 84 : (product == "TERM" ? 120 : 36);
        if (termMonths < 12 || termMonths > maxTerm)
        {
            return Reject("Term must be between 12 and " + maxTerm + " months for product " + product + ".", "BR-ELG-003", evaluated);
        }

        // BR-ELG-004 LoanApplicationForm.cs:103-110 — floor 640, LOC 660, EQUIP 620; strict <.
        evaluated.Add("BR-ELG-004");
        int minScore = 640;
        if (product == "LOC") minScore = 660;
        if (product == "EQUIP") minScore = 620;
        if (creditScore < minScore)
        {
            return Reject("Credit score " + creditScore + " below product minimum of " + minScore + ".", "BR-ELG-004", evaluated);
        }

        // BR-ELG-005 LoanApplicationForm.cs:113-120 — DTI on the base-rate estimated
        // payment; strict >; annualIncome == 0 propagates DivideByZeroException
        // exactly as the legacy form (caught only by its generic handler).
        evaluated.Add("BR-ELG-005");
        decimal estPayment = LoanCalculator.MonthlyPayment(amount, LoanCalculator.GetBaseRate(product), termMonths);
        decimal dti = (monthlyDebt + estPayment) / (annualIncome / 12m);
        decimal maxDti = product == "LOC" ? 0.40m : 0.45m;
        if (dti > maxDti)
        {
            return Reject("DTI " + LegacyCulture.Ratio3(dti) + " exceeds maximum " + LegacyCulture.Fixed2(maxDti) + ".", "BR-ELG-005", evaluated,
                dti: dti, estPayment: estPayment);
        }

        // BR-ELG-006 LoanApplicationForm.cs:123-127 — collateral must be > 0.
        evaluated.Add("BR-ELG-006");
        if (collateral <= 0m)
        {
            return Reject("Collateral value required.", "BR-ELG-006", evaluated,
                dti: dti, estPayment: estPayment);
        }

        // BR-ELG-007 LoanApplicationForm.cs:128-134 — LTV cap TERM 0.90, EQUIP 0.85,
        // else (LOC) 0.80; strict >.
        evaluated.Add("BR-ELG-007");
        decimal ltv = amount / collateral;
        decimal maxLtv = product == "TERM" ? 0.90m : (product == "EQUIP" ? 0.85m : 0.80m);
        if (ltv > maxLtv)
        {
            return Reject("LTV " + LegacyCulture.Ratio3(ltv) + " exceeds maximum " + LegacyCulture.Fixed2(maxLtv) + " for " + product + ".", "BR-ELG-007", evaluated,
                dti: dti, ltv: ltv, estPayment: estPayment);
        }

        // BR-ELG-008 LoanApplicationForm.cs:137-141 — LOC only, strict < 2 years.
        evaluated.Add("BR-ELG-008");
        if (product == "LOC" && yearsInBusiness < 2)
        {
            return Reject("Lines of credit require at least 2 years in business.", "BR-ELG-008", evaluated,
                dti: dti, ltv: ltv, estPayment: estPayment);
        }

        // BR-ELG-009 LoanApplicationForm.cs:143-147 — approval text is byte-exact,
        // including embedded newlines, three spaces between DTI and LTV, "0.000"
        // ratios and "C2" currency.
        evaluated.Add("BR-ELG-009");
        return new EligibilityResult
        {
            Decision = "APPROVED",
            ResultText = "APPROVED FOR UNDERWRITING\nDTI: " + LegacyCulture.Ratio3(dti) +
                         "   LTV: " + LegacyCulture.Ratio3(ltv) +
                         "\nEst. payment at base rate: " + LegacyCulture.Currency(estPayment),
            DeclineReason = null,
            FiredRuleId = "BR-ELG-009",
            Dti = dti,
            DtiDisplay = LegacyCulture.Ratio3(dti),
            Ltv = ltv,
            LtvDisplay = LegacyCulture.Ratio3(ltv),
            EstPayment = estPayment,
            EstPaymentDisplay = LegacyCulture.Currency(estPayment),
            ResultLabelColor = "DarkGreen",
            RuleIds = evaluated,
        };
    }

    // BR-UI-006 LoanApplicationForm.cs:161-165 — "DECLINED\n" + reason, Firebrick.
    private static EligibilityResult Reject(string reason, string firedRuleId, List<string> evaluated,
        decimal? dti = null, decimal? ltv = null, decimal? estPayment = null)
    {
        return new EligibilityResult
        {
            Decision = "DECLINED",
            ResultText = "DECLINED\n" + reason,
            DeclineReason = reason,
            FiredRuleId = firedRuleId,
            Dti = dti,
            DtiDisplay = dti is null ? null : LegacyCulture.Ratio3(dti.Value),
            Ltv = ltv,
            LtvDisplay = ltv is null ? null : LegacyCulture.Ratio3(ltv.Value),
            EstPayment = estPayment,
            EstPaymentDisplay = estPayment is null ? null : LegacyCulture.Currency(estPayment.Value),
            ResultLabelColor = "Firebrick",
            RuleIds = evaluated,
        };
    }
}
