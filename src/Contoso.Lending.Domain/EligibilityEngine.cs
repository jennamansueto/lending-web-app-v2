using System.Collections.Generic;
using System.Globalization;

namespace Contoso.Lending.Domain;

public sealed record EligibilityRequest(
    string ProductType,
    decimal Amount,
    int TermMonths,
    decimal AnnualIncome,
    decimal MonthlyDebt,
    int CreditScore,
    decimal CollateralValue,
    int YearsInBusiness);

public sealed record EligibilityResult(
    string Decision,
    string? DeclineReason,
    string FiredRuleId,
    decimal? Dti,
    decimal? Ltv,
    decimal? EstimatedPayment,
    string ResultText,
    IReadOnlyList<string> RuleIds);

/// <summary>
/// Eligibility rules extracted 1:1 from legacy
/// `src/LendingDesk/Forms/LoanApplicationForm.btnSubmit_Click` (2011 credit policy binder).
/// Statement order is behavior: BR-ELG-010 requires first-failing-rule-wins in this exact
/// sequence. All strings are formatted under en-US (<see cref="LegacyCulture"/>).
/// </summary>
public static class EligibilityEngine
{
    public static EligibilityResult Evaluate(EligibilityRequest r)
    {
        var culture = LegacyCulture.EnUs;
        string product = r.ProductType;
        var fired = new List<string>();

        // BR-ELG-001: amount >= $25,000.
        fired.Add("BR-ELG-001");
        if (r.Amount < 25000m)
            return Reject(fired, "BR-ELG-001", "Loan amount below $25,000 minimum.");

        // BR-ELG-002: amount <= $5,000,000 desk limit.
        fired.Add("BR-ELG-002");
        if (r.Amount > 5000000m)
            return Reject(fired, "BR-ELG-002", "Loan amount exceeds $5,000,000 desk limit; refer to Credit Committee.");

        // BR-ELG-003: term between 12 and the product maximum (TERM 120 / EQUIP 84 / LOC 36).
        fired.Add("BR-ELG-003");
        int maxTerm = product == "EQUIP" ? 84 : (product == "TERM" ? 120 : 36);
        if (r.TermMonths < 12 || r.TermMonths > maxTerm)
            return Reject(fired, "BR-ELG-003",
                "Term must be between 12 and " + maxTerm.ToString(culture) + " months for product " + product + ".");

        // BR-ELG-004: minimum credit score by product (TERM 640 / LOC 660 / EQUIP 620).
        fired.Add("BR-ELG-004");
        int minScore = 640;
        if (product == "LOC") minScore = 660;
        if (product == "EQUIP") minScore = 620;
        if (r.CreditScore < minScore)
            return Reject(fired, "BR-ELG-004",
                "Credit score " + r.CreditScore.ToString(culture) + " below product minimum of " + minScore.ToString(culture) + ".");

        // BR-ELG-012: the DTI payment is priced at the BASE rate, not the priced rate.
        decimal estPayment = AmortizationEngine.MonthlyPayment(r.Amount, PricingEngine.GetBaseRate(product), r.TermMonths);
        // BR-ELG-013: DTI = (monthly debt service + estimated payment) / (annual income / 12).
        // Legacy quirk: no guard against a zero annual income; the division is left as-is.
        decimal dti = (r.MonthlyDebt + estPayment) / (r.AnnualIncome / 12m);

        // BR-ELG-005: DTI cap 0.40 (LOC) / 0.45.
        fired.Add("BR-ELG-005");
        decimal maxDti = product == "LOC" ? 0.40m : 0.45m;
        if (dti > maxDti)
            return Reject(fired, "BR-ELG-005",
                "DTI " + dti.ToString("0.000", culture) + " exceeds maximum " + maxDti.ToString("0.00", culture) + ".",
                dti, null, estPayment);

        // BR-ELG-006: collateral value must be present (> 0). Evaluated after DTI, so a
        // zero-collateral application still reports a DTI.
        fired.Add("BR-ELG-006");
        if (r.CollateralValue <= 0m)
            return Reject(fired, "BR-ELG-006", "Collateral value required.", dti, null, estPayment);

        // BR-ELG-014: LTV = amount / collateral value.
        decimal ltv = r.Amount / r.CollateralValue;

        // BR-ELG-007: LTV cap 0.90 (TERM) / 0.85 (EQUIP) / 0.80 (LOC).
        // Legacy quirk: the comparison uses the raw quotient while the message prints it
        // rounded to 3dp, so an LTV of 0.9000000... is declined with the text "LTV 0.900
        // exceeds maximum 0.90".
        fired.Add("BR-ELG-007");
        decimal maxLtv = product == "TERM" ? 0.90m : (product == "EQUIP" ? 0.85m : 0.80m);
        if (ltv > maxLtv)
            return Reject(fired, "BR-ELG-007",
                "LTV " + ltv.ToString("0.000", culture) + " exceeds maximum " + maxLtv.ToString("0.00", culture) + " for " + product + ".",
                dti, ltv, estPayment);

        // BR-ELG-008: lines of credit require at least 2 years in business.
        fired.Add("BR-ELG-008");
        if (product == "LOC" && r.YearsInBusiness < 2)
            return Reject(fired, "BR-ELG-008", "Lines of credit require at least 2 years in business.", dti, ltv, estPayment);

        // BR-ELG-009 / BR-UI-002: approval panel text.
        fired.Add("BR-ELG-009");
        string approvedText = "APPROVED FOR UNDERWRITING\nDTI: " + dti.ToString("0.000", culture) +
                              "   LTV: " + ltv.ToString("0.000", culture) +
                              "\nEst. payment at base rate: " + estPayment.ToString("C2", culture);
        return new EligibilityResult("APPROVED", null, "BR-ELG-009", dti, ltv, estPayment, approvedText, fired);
    }

    private static EligibilityResult Reject(
        List<string> fired,
        string ruleId,
        string reason,
        decimal? dti = null,
        decimal? ltv = null,
        decimal? estPayment = null) =>
        // BR-UI-002: declines render as "DECLINED\n<reason>".
        new("DECLINED", reason, ruleId, dti, ltv, estPayment, "DECLINED\n" + reason, fired);
}
