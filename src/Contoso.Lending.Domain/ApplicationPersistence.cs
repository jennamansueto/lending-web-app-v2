namespace Contoso.Lending.Domain;

/// <summary>
/// BR-ELG-010 LoanApplicationForm.cs:166-181 (SaveApplication) — the values an
/// approved application persists. Only approved applications are saved
/// (SaveApplication is called at :143, before the approval label is set).
/// </summary>
public sealed class PersistedApplication
{
    public int BorrowerId { get; init; }
    public required string ProductType { get; init; }
    public decimal Amount { get; init; }
    public int TermMonths { get; init; }
    public int CreditScore { get; init; }
    public decimal Dti { get; init; }
    public decimal Ltv { get; init; }
    public required string Status { get; init; }
}

public static class ApplicationPersistence
{
    public const string AppIdSource = "SEQ_LOAN_APPLICATION.NEXTVAL";
    public const string CreatedAtSource = "database SYSDATE";

    public static PersistedApplication Build(int borrowerId, string productType, decimal amount,
        int termMonths, int creditScore, decimal dti, decimal ltv)
    {
        return new PersistedApplication
        {
            BorrowerId = borrowerId,
            ProductType = productType,
            Amount = amount,
            TermMonths = termMonths,
            CreditScore = creditScore,
            // BR-ELG-010 LoanApplicationForm.cs:179-180 — plain Math.Round(x, 4):
            // banker's rounding (MidpointRounding.ToEven), unlike the AwayFromZero
            // rounding used everywhere else. Do not "fix".
            Dti = Math.Round(dti, 4),
            Ltv = Math.Round(ltv, 4),
            // BR-ELG-010 LoanApplicationForm.cs:173 — literal 'SUBMITTED'.
            Status = "SUBMITTED",
        };
    }
}
