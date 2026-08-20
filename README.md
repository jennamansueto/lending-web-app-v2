# Contoso Commercial Lending — Web Application (v2)

Modern 4-layer replacement for the legacy [`lending-desktop-app`](https://github.com/jennamansueto/lending-desktop-app)
(.NET Framework 4.7.2 WinForms fat client on Oracle, with business logic in UI code-behind and
PL/SQL).

| Layer | Technology | Responsibility |
|---|---|---|
| Data / system of record | Postgres 16 (Lakebase stand-in) | tables, keys, declarative constraints, migration + checksum tooling |
| Service / business logic | .NET 8 | **all** business rules (eligibility, pre-qualification, pricing, amortization, servicing) |
| Workflow / orchestration | Temporal (.NET SDK) | sequencing and state only — zero business logic (enforced by a purity test) |
| UI / presentation | Angular micro frontends | one remote per legacy screen, composed in a shell via Module Federation |

The migration is behavior-preserving and machine-proven: see [`docs/architecture.md`](docs/architecture.md),
the service contract in [`docs/api-contract.md`](docs/api-contract.md), and the four parity levels
(UI, service, data, end-to-end) verified by the parity harness and dashboard.

Run the live parity dashboard with `dotnet run --project tools/parity-dashboard` from the repo root.
It streams `parity/run-all.sh` progress and renders the structured `parity/parity-dashboard.json`
snapshot. For a one-shot CLI run, use `PARITY_LEVELS=L2,L3,L4 ./parity/run-all.sh`.

> Legacy quirks and known bugs are reproduced deliberately and documented — never "fixed".
