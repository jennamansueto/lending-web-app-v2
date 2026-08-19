using System.Net.Http.Json;
using System.Text.Json;
using Contoso.Lending.Workflow.Contracts;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Contoso.Lending.Workflow.Activities;

/// <summary>
/// The workflow layer's only door to business behavior: one activity per layer-2 endpoint in
/// <c>docs/api-contract.md</c>. Every activity posts the caller-supplied payload and returns the
/// service response as-is. No arithmetic, no comparisons, no thresholds, no rounding and no string
/// construction happen here — decisions, <c>resultText</c> and every number are the service's.
/// </summary>
public class LendingServiceActivities(HttpClient client)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Activity]
    public Task<EligibilityResult> EvaluateEligibility(EligibilityRequest request) =>
        PostAsync<EligibilityRequest, EligibilityResult>("/api/eligibility/evaluate", request);

    [Activity]
    public Task<PersistApplicationResult> PersistApplication(EligibilityRequest request) =>
        PostAsync<EligibilityRequest, PersistApplicationResult>("/api/applications", request);

    [Activity]
    public Task<PricingQuoteResult> GetPricingQuote(PricingQuoteRequest request) =>
        PostAsync<PricingQuoteRequest, PricingQuoteResult>("/api/pricing/quote", request);

    [Activity]
    public Task<BookLoanResult> BookLoan(BookLoanRequest request) =>
        PostAsync<BookLoanRequest, BookLoanResult>("/api/loans", request);

    [Activity]
    public async Task<PayoffQuoteResult> GetPayoffQuote(PayoffQuoteRequestArgs request)
    {
        var path = $"/api/loans/{request.LoanId}/payoff?asOf={Uri.EscapeDataString(request.AsOf)}";
        using var response = await client.GetAsync(path).ConfigureAwait(false);
        return await ReadAsync<PayoffQuoteResult>(response, path).ConfigureAwait(false);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request)
    {
        using var response = await client.PostAsJsonAsync(path, request, Json).ConfigureAwait(false);
        return await ReadAsync<TResponse>(response, path).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string path)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // 4xx is the service rejecting the payload: retrying cannot change the answer.
            var message = $"{(int)response.StatusCode} from {path}: {body}";
            throw new ApplicationFailureException(
                message,
                nonRetryable: (int)response.StatusCode is >= 400 and < 500);
        }

        return JsonSerializer.Deserialize<T>(body, Json)
            ?? throw new ApplicationFailureException($"Empty body from {path}", nonRetryable: true);
    }
}

public record BookLoanRequest(
    long AppId,
    long BorrowerId,
    string ProductType,
    decimal Principal,
    decimal AnnualRate,
    int TermMonths,
    decimal OrigFee,
    string FundedDate);

public record BookLoanResult(long LoanId);

public record PayoffQuoteRequestArgs(long LoanId, string AsOf);
