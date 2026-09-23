using System.Net;
using System.Text.Json;

namespace Contoso.Lending.ApiTests;

/// <summary>
/// Regression coverage for the three endpoints that read the `timestamp(0)` date columns of the
/// layer-1 schema. They returned HTTP 500 ("Reading as 'System.DateOnly' is not supported for
/// fields having DataTypeName 'timestamp without time zone'") until the reader converted through
/// <see cref="DateTime"/>; these tests also pin the documented date format (`yyyy-MM-dd`).
/// </summary>
[Collection(LegacyDateSchemaCollection.Name)]
public sealed class LegacyDateColumnTests(LegacyDateSchemaFixture fixture)
{
    [Fact]
    public async Task Schedule_json_returns_iso_due_dates()
    {
        var response = await fixture.Client.GetAsync("/api/loans/5001/schedule");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = document.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);

        Assert.Equal(1, rows[0].GetProperty("periodNo").GetInt32());
        Assert.Equal("2024-02-29", rows[0].GetProperty("dueDate").GetString());
        Assert.Equal(5037.56m, rows[0].GetProperty("paymentAmt").GetDecimal());
        Assert.Equal(5012.44m, rows[0].GetProperty("balanceAfter").GetDecimal());

        Assert.Equal(2, rows[1].GetProperty("periodNo").GetInt32());
        Assert.Equal("2024-03-31", rows[1].GetProperty("dueDate").GetString());
        Assert.Equal(0.00m, rows[1].GetProperty("balanceAfter").GetDecimal());
    }

    [Fact]
    public async Task Schedule_csv_returns_legacy_header_and_crlf_rows()
    {
        var response = await fixture.Client.GetAsync("/api/loans/5001/schedule.csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);

        Assert.Equal(
            "PERIOD_NO,DUE_DATE,PAYMENT_AMT,INTEREST_AMT,PRINCIPAL_AMT,BALANCE_AFTER\r\n" +
            "1,2024-02-29,5037.56,50.00,4987.56,5012.44\r\n" +
            "2,2024-03-31,5037.50,25.06,5012.44,0.00\r\n",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Payoff_quotes_from_the_schedule_and_recorded_late_fees()
    {
        var response = await fixture.Client.GetAsync("/api/loans/5001/payoff?asOf=2024-03-15");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var payoff = document.RootElement;
        Assert.Equal(5001, payoff.GetProperty("loanId").GetInt32());
        Assert.Equal("2024-03-15", payoff.GetProperty("asOf").GetString());
        // BR-SVC-006/007/009/010 over the seeded rows: 5012.44 + 15 days actual/365 at 6.000% + 150.00.
        Assert.Equal(5012.44m, payoff.GetProperty("balance").GetDecimal());
        Assert.Equal(12.36m, payoff.GetProperty("accruedInterest").GetDecimal());
        Assert.Equal(150.00m, payoff.GetProperty("unpaidLateFees").GetDecimal());
        Assert.Equal(5174.80m, payoff.GetProperty("payoff").GetDecimal());
    }

    [Fact]
    public async Task Payoff_of_an_unknown_loan_is_not_found()
    {
        var response = await fixture.Client.GetAsync("/api/loans/999999/payoff?asOf=2024-03-15");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
