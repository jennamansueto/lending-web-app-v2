# Layer 3 — Workflow / Orchestration (Temporal .NET SDK)

Sequencing and state for the origination process. **Zero business logic**: no calculation, no
threshold, no rounding, no money formatting and no decision — every number and every
user-visible string comes from the layer-2 service API (`docs/api-contract.md`) and is passed
through verbatim. The boundary is enforced by an executing test, not by convention
(`tests/Contoso.Lending.WorkflowTests/WorkflowPurityTests.cs`).

```
UI (layer 4) ──start/signal/query──▶ Temporal ──activities (HTTP)──▶ Service API (layer 2) ──▶ Postgres (layer 1)
```

## Contents

| Path | Role |
|---|---|
| `src/Contoso.Lending.Workflow/Workflows/LoanApplicationWorkflow.cs` | submit → evaluate eligibility → persist on approval → hand off to underwriting (→ optional booking) |
| `src/Contoso.Lending.Workflow/Workflows/PayoffQuoteWorkflow.cs` | payoff quote → holds the quote as state for downstream delivery |
| `src/Contoso.Lending.Workflow/Activities/LendingServiceActivities.cs` | one thin HTTP activity per layer-2 endpoint; deserialize and return, nothing else |
| `src/Contoso.Lending.Workflow/Contracts/ServiceContracts.cs` | transport DTOs mirroring `docs/api-contract.md` |
| `src/Contoso.Lending.Workflow/WorkerHost.cs`, `Program.cs`, `Cli.cs` | worker host and CLI (`worker` \| `demo` \| `payoff`) |
| `src/Contoso.Lending.Workflow/DemoRunner.cs`, `demo/scenarios.json` | end-to-end demo driver + the golden-corpus scenarios it asserts |
| `src/Contoso.Lending.Workflow/docker-compose.workflow.yml` | Temporal dev server (gRPC 7233, UI 8233) |
| `src/Contoso.Lending.Workflow/scripts/run-demo.sh` | one-command approved + declined end-to-end run |
| `tests/Contoso.Lending.WorkflowTests/` | purity test + workflow behaviour tests (stubbed service API) |

Configuration is environment-driven: `TEMPORAL_ADDRESS` (default `localhost:7233`),
`TEMPORAL_NAMESPACE` (default `default`), `LENDING_API_BASE` (default `http://localhost:5080`).
Task queue: `lending-origination`.

## LoanApplicationWorkflow

| Step | Activity → endpoint | Workflow's own contribution |
|---|---|---|
| evaluate | `EvaluateEligibility` → `POST /api/eligibility/evaluate` | sets status `EVALUATING`; retries transient failures |
| decline | — | status becomes the service's `decision`; nothing is persisted, mirroring the legacy screen |
| persist | `PersistApplication` → `POST /api/applications` | forwards the **intake unchanged**; the service recomputes and rounds what it stores (BR-ELG-010) |
| hand off | — | status `AWAITING_UNDERWRITING`; completes here unless `UnderwritingDecisionTimeout` is set |
| underwriting | signal `SubmitUnderwritingDecision` | waits for the signal, then routes on the string the underwriter sent |
| book | `GetPricingQuote` → `POST /api/pricing/quote`, `BookLoan` → `POST /api/loans` | passes the service's `rate`, `originationFee`, `monthlyPayment` straight into booking and the result |

State is observable through the `Status` query. Result fields (`decision`, `resultText`,
`declineReason`, `firedRuleId`, `dti`, `ltv`, `estimatedPayment`, `rate`, `originationFee`,
`payment`) are copies of service output; `appId` / `loanId` are the ids the service returned.

The two string comparisons in the workflow (`decision != "APPROVED"`,
`decision != "BOOK"`) are routing on values produced elsewhere — the service's decision and the
underwriter's signal. Neither derives a decision from application data, which is what the layer
boundary forbids.

Retries and timeouts are the only "logic" this layer owns: 30 s start-to-close, 1 s initial
backoff doubling to 10 s, 5 attempts. HTTP 4xx from the service is raised as a non-retryable
`ApplicationFailureException` (the payload cannot become valid by retrying); 5xx is retried.

## Running it locally

```bash
# 1. data layer (from the Postgres data-layer PR): Postgres + schema/migration
docker compose up -d postgres
docker exec -i lending-postgres psql -U lending -d lending -v ON_ERROR_STOP=1 -f /database/postgres/schema.sql
dotnet run --project tools/migrator -- migrate     # or seed a borrower for a demo-only run

# 2. service layer
ConnectionStrings__Lending="Host=localhost;Port=5432;Database=lending;Username=lending;Password=lending_pw_2014" \
  dotnet run --project src/Contoso.Lending.Api      # http://localhost:5080

# 3. Temporal dev server
docker compose -f src/Contoso.Lending.Workflow/docker-compose.workflow.yml up -d --wait

# 4. worker
dotnet run --project src/Contoso.Lending.Workflow -- worker

# 5. the end-to-end demo (starts Temporal + worker itself, asserts against the golden corpus)
src/Contoso.Lending.Workflow/scripts/run-demo.sh

# payoff quote through the workflow
dotnet run --project src/Contoso.Lending.Workflow -- payoff --loan-id 5000 --as-of 2026-08-19
```

The Temporal Web UI is at <http://localhost:8233>.

## Demo scenarios

`demo/scenarios.json` is copied verbatim from the legacy golden corpus
`parity/golden/BR-ELG-009_eligibility_decision.json`:

| Scenario | Corpus case | Expected `resultText` |
|---|---|---|
| `approved` | approval: TERM typical application | `APPROVED FOR UNDERWRITING\nDTI: 0.139   LTV: 0.500\nEst. payment at base rate: $1,956.61` |
| `declined` | precedence 4/9: term repaired -> credit score fires | `DECLINED\nCredit score 500 below product minimum of 660.` |

The runner compares `decision`, `resultText`, `declineReason` and `firedRuleId` with
`string.Equals(..., StringComparison.Ordinal)` — exact equality, no normalization, no epsilon,
no tolerance anywhere in this layer (there is no floating-point comparison at all: money and
ratios are `decimal` and are never compared, only carried).

## The purity test

`WorkflowPurityTests` checks three independent surfaces, so a violation cannot slip past one of
them:

1. the declared `PackageReference` / `ProjectReference` set in `Contoso.Lending.Workflow.csproj`;
2. the compiled assembly's `GetReferencedAssemblies()` (catches actual code use);
3. every `*.dll` in the build output (catches transitive package pulls).

Forbidden name fragments: `Npgsql`, `Oracle`, `Dapper`, `EntityFramework(Core)`,
`System.Data.SqlClient`, `Microsoft.Data.SqlClient`, `Contoso.Lending.Domain`,
`Contoso.Lending.Data`, `Contoso.Lending.Migrator`. Adding any of them turns the suite red — see
the PR description for the demonstrated failure and revert.

## Adding a workflow

1. Add the endpoint DTOs to `Contracts/ServiceContracts.cs` exactly as `docs/api-contract.md`
   describes them.
2. Add a one-line activity to `LendingServiceActivities` (post/get, return the response).
3. Sequence the activities in a `[Workflow]` class. If you find yourself writing an arithmetic
   operator, a numeric comparison, a `Math.Round` or a formatted money string, the logic belongs
   in layer 2 — add or extend a service endpoint instead.
4. Register the workflow in `WorkerHost` and cover it in `tests/Contoso.Lending.WorkflowTests`.
