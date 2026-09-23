using Temporalio.Activities;

namespace Contoso.Lending.Workflow;

/// <summary>
/// One activity per service endpoint the origination process needs. Each is a pass-through:
/// serialize the request, call the endpoint, return what came back.
/// </summary>
public sealed class OriginationActivities(ServiceApiClient client)
{
    [Activity]
    public Task<EligibilityResponse> EvaluateEligibilityAsync(EligibilityRequest request) =>
        client.PostAsync<EligibilityRequest, EligibilityResponse>(
            "/api/eligibility/evaluate", request, ActivityExecutionContext.Current.CancellationToken);

    [Activity]
    public Task<CreateApplicationResponse> PersistApplicationAsync(CreateApplicationRequest request) =>
        client.PostAsync<CreateApplicationRequest, CreateApplicationResponse>(
            "/api/applications", request, ActivityExecutionContext.Current.CancellationToken);

    [Activity]
    public Task<QuoteResponse> PriceLoanAsync(QuoteRequest request) =>
        client.PostAsync<QuoteRequest, QuoteResponse>(
            "/api/pricing/quote", request, ActivityExecutionContext.Current.CancellationToken);

    [Activity]
    public Task<BookLoanResponse> BookLoanAsync(BookLoanRequest request) =>
        client.PostAsync<BookLoanRequest, BookLoanResponse>(
            "/api/loans", request, ActivityExecutionContext.Current.CancellationToken);
}
