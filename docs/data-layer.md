# Layer 1 — Data / System of Record (Postgres 16)

Behaviour-preserving port of the legacy Oracle `LENDING` schema
([`lending-desktop-app/database/schema.sql`](https://github.com/jennamansueto/lending-desktop-app/blob/main/database/schema.sql))
to Postgres 16, plus the migration and parity tooling that proves the move is exact.

The authoritative input is the Phase-1 profile `docs/data-profile.md` (legacy repo) and
its machine-readable companion `database/checks/`. Every per-column type below is the
"Postgres recommendation" from that profile, unchanged.

| Artefact | Purpose |
|---|---|
| `database/postgres/schema.sql` | The Postgres DDL: five tables, all keys, constraints, defaults, indexes, sequences |
| `database/checks/checksum_postgres.sql` | Phase-1 checksum harness (spec_version 1.0), ported verbatim from the legacy repo |
| `database/checks/baseline-oracle.json` | Committed Phase-1 Oracle baseline: row counts, canonical numeric sums, hash chains, sequence positions |
| `database/checks/baseline-oracle-rows.tsv` | Committed per-row canonical strings + hashes, for localising a mismatch to one row |
| `tools/migrator/` | .NET 8 CLI: `migrate` (re-runnable copy) and `verify` (zero-tolerance comparison) |
| `docker-compose.yml` | Local Postgres 16 (`lending-postgres`, demo credentials) |

## What deliberately does *not* live here

`PKG_LENDING` (`CALC_LATE_FEE`, `GET_PAYOFF_AMOUNT` — BR-SVC-001/002) is **not** ported
to Postgres. Those are business rules: the $150 late-fee cap, the 5 % rate, the 10-day
grace boundary and the payoff aggregation belong to the .NET 8 service layer per
`docs/architecture.md`. The database keeps no procedural logic and no PL/pgSQL rule
functions. Likewise absent, on purpose:

- no `CHECK` on `CREDIT_SCORE`, `AMOUNT`, `PRINCIPAL`, `TERM_MONTHS`, `ANNUAL_RATE`,
  `DAYS_LATE` or `LATE_FEE` (BR-DAT-027) — the legacy database accepts values the
  application rejects, and tightening that would reject rows Oracle accepts;
- no `CHECK` on `loan.product_type`, even though `loan_application.product_type` has one
  (BR-DAT-017: the legacy asymmetry is preserved);
- no `CHECK` on either `status` column, no enum types (BR-DAT-010, BR-DAT-016);
- no `UNIQUE` on `loan.app_id` and no `(loan_id, period_no)` uniqueness on `payment`
  (BR-DAT-014, BR-DAT-021);
- no identity columns and no `DEFAULT nextval(...)`: the client supplies every key today
  (BR-DAT-001, hazard H-01);
- no `payment_amt = interest_amt + principal_amt` constraint (holds in data, unenforced);
- no schedule generator — `payment_schedule` rows are copied, never regenerated (H-02).

## Schema mapping

Identifiers are the lower_snake_case of the legacy names; nothing is renamed. Column
order is the legacy declaration order, which the checksum spec depends on.

### `BORROWER` → `borrower`

| # | Legacy column | Oracle type | Postgres column | Postgres type | Constraints / default |
|---|---|---|---|---|---|
| 1 | `BORROWER_ID` | `NUMBER(10)` | `borrower_id` | `numeric(10)` | `pk_borrower` PK, caller-assigned |
| 2 | `LEGAL_NAME` | `VARCHAR2(200)` | `legal_name` | `varchar(200)` | NOT NULL |
| 3 | `TAX_ID` | `VARCHAR2(20)` | `tax_id` | `varchar(20)` | NOT NULL, `uq_borrower_tax_id` UNIQUE |
| 4 | `CREDIT_SCORE` | `NUMBER(4)` | `credit_score` | `numeric(4)` | NOT NULL, no range CHECK |
| 5 | `DEPOSIT_BALANCE` | `NUMBER(14,2)` | `deposit_balance` | `numeric(14,2)` | NOT NULL DEFAULT 0 |
| 6 | `YEARS_IN_BUSINESS` | `NUMBER(3)` | `years_in_business` | `numeric(3)` | NOT NULL DEFAULT 0 |
| 7 | `CREATED_AT` | `DATE` DEFAULT `SYSDATE` | `created_at` | `timestamp(0)` | NOT NULL DEFAULT `localtimestamp(0)` |

### `LOAN_APPLICATION` → `loan_application`

| # | Legacy column | Oracle type | Postgres column | Postgres type | Constraints / default |
|---|---|---|---|---|---|
| 1 | `APP_ID` | `NUMBER(10)` | `app_id` | `numeric(10)` | `pk_loan_application` PK |
| 2 | `BORROWER_ID` | `NUMBER(10)` | `borrower_id` | `numeric(10)` | NOT NULL, `fk_loan_application_borrower` |
| 3 | `PRODUCT_TYPE` | `VARCHAR2(10)` | `product_type` | `varchar(10)` | NOT NULL, `ck_loan_application_product_type` `IN ('TERM','LOC','EQUIP')` |
| 4 | `AMOUNT` | `NUMBER(14,2)` | `amount` | `numeric(14,2)` | NOT NULL |
| 5 | `TERM_MONTHS` | `NUMBER(4)` | `term_months` | `numeric(4)` | NOT NULL |
| 6 | `CREDIT_SCORE` | `NUMBER(4)` | `credit_score` | `numeric(4)` | NOT NULL |
| 7 | `DTI` | `NUMBER(6,4)` | `dti` | `numeric(6,4)` | nullable (ratio, not percent) |
| 8 | `LTV` | `NUMBER(6,4)` | `ltv` | `numeric(6,4)` | nullable |
| 9 | `STATUS` | `VARCHAR2(20)` | `status` | `varchar(20)` | NOT NULL DEFAULT `'SUBMITTED'`, no CHECK |
| 10 | `CREATED_AT` | `DATE` DEFAULT `SYSDATE` | `created_at` | `timestamp(0)` | NOT NULL DEFAULT `localtimestamp(0)` |

### `LOAN` → `loan`

| # | Legacy column | Oracle type | Postgres column | Postgres type | Constraints / default |
|---|---|---|---|---|---|
| 1 | `LOAN_ID` | `NUMBER(10)` | `loan_id` | `numeric(10)` | `pk_loan` PK |
| 2 | `APP_ID` | `NUMBER(10)` | `app_id` | `numeric(10)` | nullable, non-unique, `fk_loan_application` |
| 3 | `BORROWER_ID` | `NUMBER(10)` | `borrower_id` | `numeric(10)` | NOT NULL, `fk_loan_borrower` |
| 4 | `PRODUCT_TYPE` | `VARCHAR2(10)` | `product_type` | `varchar(10)` | NOT NULL, **no** CHECK |
| 5 | `PRINCIPAL` | `NUMBER(14,2)` | `principal` | `numeric(14,2)` | NOT NULL |
| 6 | `ANNUAL_RATE` | `NUMBER(6,3)` | `annual_rate` | `numeric(6,3)` | NOT NULL, percent convention, 3 decimals |
| 7 | `TERM_MONTHS` | `NUMBER(4)` | `term_months` | `numeric(4)` | NOT NULL |
| 8 | `ORIG_FEE` | `NUMBER(12,2)` | `orig_fee` | `numeric(12,2)` | NOT NULL |
| 9 | `FUNDED_DATE` | `DATE` | `funded_date` | `timestamp(0)` | NOT NULL |
| 10 | `STATUS` | `VARCHAR2(20)` | `status` | `varchar(20)` | NOT NULL DEFAULT `'ACTIVE'`, no CHECK |

### `PAYMENT_SCHEDULE` → `payment_schedule`

| # | Legacy column | Oracle type | Postgres column | Postgres type | Constraints |
|---|---|---|---|---|---|
| 1 | `LOAN_ID` | `NUMBER(10)` | `loan_id` | `numeric(10)` | PK1, `fk_payment_schedule_loan` |
| 2 | `PERIOD_NO` | `NUMBER(4)` | `period_no` | `numeric(4)` | PK2 |
| 3 | `DUE_DATE` | `DATE` | `due_date` | `timestamp(0)` | NOT NULL |
| 4 | `PAYMENT_AMT` | `NUMBER(12,2)` | `payment_amt` | `numeric(12,2)` | NOT NULL |
| 5 | `INTEREST_AMT` | `NUMBER(12,2)` | `interest_amt` | `numeric(12,2)` | NOT NULL |
| 6 | `PRINCIPAL_AMT` | `NUMBER(12,2)` | `principal_amt` | `numeric(12,2)` | NOT NULL |
| 7 | `BALANCE_AFTER` | `NUMBER(14,2)` | `balance_after` | `numeric(14,2)` | NOT NULL (exact `0.00` in final periods preserved) |

`PK_PAYMENT_SCHEDULE` keeps its legacy name as `pk_payment_schedule`; it is the only
user-named constraint in the legacy schema.

### `PAYMENT` → `payment`

| # | Legacy column | Oracle type | Postgres column | Postgres type | Constraints / default |
|---|---|---|---|---|---|
| 1 | `PAYMENT_ID` | `NUMBER(10)` | `payment_id` | `numeric(10)` | `pk_payment` PK |
| 2 | `LOAN_ID` | `NUMBER(10)` | `loan_id` | `numeric(10)` | NOT NULL, `fk_payment_loan` |
| 3 | `PERIOD_NO` | `NUMBER(4)` | `period_no` | `numeric(4)` | NOT NULL, **not** a FK to `payment_schedule` |
| 4 | `PAID_DATE` | `DATE` | `paid_date` | `timestamp(0)` | NOT NULL |
| 5 | `AMOUNT` | `NUMBER(12,2)` | `amount` | `numeric(12,2)` | NOT NULL |
| 6 | `DAYS_LATE` | `NUMBER(4)` | `days_late` | `numeric(4)` | NOT NULL DEFAULT 0, stored not derived |
| 7 | `LATE_FEE` | `NUMBER(10,2)` | `late_fee` | `numeric(10,2)` | NOT NULL DEFAULT 0, stored not derived, cap not constrained |

### Indexes and referential graph

| Legacy index | Postgres | Kind |
|---|---|---|
| `SYS_C008649` (PK) | `pk_borrower` | implicit unique |
| `SYS_C008650` (UNIQUE) | `uq_borrower_tax_id` | implicit unique |
| `SYS_C008659` (PK) | `pk_loan_application` | implicit unique |
| `IX_APP_BORROWER` | `ix_app_borrower` | explicit non-unique B-tree |
| `SYS_C008669` (PK) | `pk_loan` | implicit unique |
| `IX_LOAN_BORROWER` | `ix_loan_borrower` | explicit non-unique B-tree |
| `PK_PAYMENT_SCHEDULE` | `pk_payment_schedule` | implicit unique, `(loan_id, period_no)` |
| `SYS_C008687` (PK) | `pk_payment` | implicit unique |
| `IX_PAYMENT_LOAN` | `ix_payment_loan` | explicit non-unique B-tree |

All four foreign keys are `NO ACTION`, `NOT DEFERRABLE`: no cascades, no `ON UPDATE`
(BR-DAT-025). Constraints are given explicit names because the Oracle `SYS_Cnnnnnn`
names are not stable across rebuilds (hazard H-04) and are excluded from the checksum.

## Sequence handling

| Sequence | `START WITH` | Legacy `LAST_NUMBER` | Postgres DDL | Post-`migrate` position |
|---|---|---|---|---|
| `SEQ_LOAN_APPLICATION` | 1000 | 1000 | `CREATE SEQUENCE seq_loan_application START WITH 1000 INCREMENT BY 1 NO CYCLE CACHE 20` | next `nextval()` = 1000 |
| `SEQ_LOAN` | 5000 | 5000 | `CREATE SEQUENCE seq_loan START WITH 5000 INCREMENT BY 1 NO CYCLE CACHE 20` | next `nextval()` = 5000 |
| `SEQ_PAYMENT` | 90000 | 90000 | `CREATE SEQUENCE seq_payment START WITH 90000 INCREMENT BY 1 NO CYCLE CACHE 20` | next `nextval()` = 90000 |

`migrate` reads `USER_SEQUENCES.LAST_NUMBER` from Oracle and re-asserts each position
with `setval(seq, last_number, false)`, so `nextval()` returns exactly the number Oracle
would have returned. `verify` compares the Oracle position, the committed baseline
position and the live Postgres position; a disagreement is a failure.

Sequences are **not** converted to identity columns or column defaults: today the client
always supplies keys explicitly, and attaching a default would change who assigns them.

## Hazards H-01 … H-08

| # | Hazard | Handling in this layer |
|---|---|---|
| H-01 | `SEQ_PAYMENT` sits at 90000 while `MAX(PAYMENT_ID)` = 90005 — the first `nextval()` collides with an existing PK | **Reproduced, not fixed.** `seq_payment` is created at 90000 and `migrate` re-positions it to the legacy `LAST_NUMBER` on every run. The collision is documented in `database/postgres/schema.sql` and asserted by `verify`. Resynchronising it would be a behaviour change and needs a separate, approved decision; the service layer must keep supplying `payment_id` explicitly, exactly as the legacy client does. `SEQ_LOAN_APPLICATION` (1000) and `SEQ_LOAN` (5000) are likewise carried over verbatim even though they sit above their current maxima. |
| H-02 | Oracle `ADD_MONTHS` end-of-month clamping differs from `+ interval '1 month'` | No schedule is ever generated here: `payment_schedule` rows are copied byte-for-byte from Oracle, and the migrator contains no date arithmetic. The `due_date` chain (e.g. loan 1's `2021-05-31`, which interval arithmetic would render `2021-05-30`) is therefore preserved, and `verify` compares `DUE_DATE` `MIN`/`MAX` plus the per-row chain. Any future generator belongs to the service layer and must reimplement `ADD_MONTHS`. |
| H-03 | Oracle `ROUND` is half-away-from-zero; `round(double precision)` in Postgres is half-to-even | Every money, rate and ratio column is `numeric(p,s)` — never `float`/`double precision`/`real` — so Postgres arithmetic and `round()` stay exact and half-away-from-zero. No rounding happens during migration: values are copied as decimals through binary `COPY`. |
| H-04 | `SYS_Cnnnnnn` constraint names are unstable | The Postgres DDL names every constraint explicitly (`pk_*`, `uq_*`, `fk_*`, `ck_*`); names are excluded from the checksum, so a legacy rebuild cannot break parity. |
| H-05 | `DATE` → `date` would silently drop the default's time-of-day | All four date-bearing columns map to `timestamp(0)` (1-second resolution, no timezone), and the two `SYSDATE` defaults become `localtimestamp(0)`. Migration copies `DateTime` values unchanged; the canonical token is `YYYY-MM-DD HH24:MI:SS` on both sides, so an invented time component would fail `verify`. |
| H-06 | `VARCHAR2(n)` counts bytes, `varchar(n)` counts characters | Lengths are carried over as declared (`varchar(200)`, `varchar(20)`, `varchar(10)`), keeping the limit as a validation rule. Identical for the current all-ASCII data; the divergence for multi-byte input is documented rather than papered over (no `text`, no widened limits). |
| H-07 | Oracle treats `''` as `NULL`, Postgres does not | Nothing in this layer writes empty strings: `migrate` copies `NULL` as `NULL` (`WriteNull`) and non-null text verbatim. The canonical `\N` token would differ from an empty token, so an empty string introduced by any writer shows up as a chain mismatch. Service-layer writers must keep storing `NULL`, not `''`. |
| H-08 | `NUMBER` scale must be declared, not inferred | Scales are per-column: `numeric(14,2)`/`numeric(12,2)`/`numeric(10,2)` money, `numeric(6,3)` rate, `numeric(6,4)` ratios, `numeric(p)` integers. `verify` formats every sum with the column's own mask, so a wrong scale (e.g. a uniform `numeric(14,2)`) fails immediately instead of silently rounding. |

## Verification design

`verify` is exact: every comparison is a string or integer equality, there is no epsilon
and no tolerance anywhere in the code. Per table it compares Oracle vs Postgres and
Postgres vs the committed baseline on:

1. `row_count`;
2. per-numeric-column `SUM`, `MIN` and `MAX`, each formatted with the column's own scale
   mask (`FM…0.00`, `FM…0.000`, `FM…0.0000`) so scale is part of the comparison;
3. per-date `MIN`/`MAX` as `YYYY-MM-DD HH24:MI:SS`;
4. the spec_version 1.0 row-hash chain (SHA-256, uppercase hex, seeded with 64 zeros,
   rows ordered by primary key);
5. `delimiter_collisions` (must be 0);
6. the three sequence positions.

The Postgres side of (1), (2) and (4) is produced by executing the committed
`database/checks/checksum_postgres.sql` harness verbatim, so the two sides use the
Phase-1 specification implementation rather than ad-hoc SQL. The Oracle side uses
`STANDARD_HASH(..., 'SHA256')` over the same canonical row string, with
`NLS_NUMERIC_CHARACTERS`, `NLS_DATE_FORMAT`, `NLS_SORT` and `NLS_COMP` pinned exactly as
the Phase-1 harness pins them. Any mismatch is printed and the process exits `1`.

To localise a failure to a single row, diff a per-row Postgres report against
`database/checks/baseline-oracle-rows.tsv`.

## Exact reproduction commands

```bash
# 1. Legacy Oracle system of record (read-only reference), from the legacy repo.
git clone https://github.com/jennamansueto/lending-desktop-app.git
cd lending-desktop-app
docker compose up -d          # gvenzl/oracle-free:23-slim on localhost:1521/FREEPDB1
./database/setup-db.sh        # schema + PKG_LENDING + seed data
./database/checks/run-checks.sh --stdout json | diff - database/checks/baseline-oracle.json
cd ..

# 2. Postgres 16 target, from this repo.
git clone https://github.com/jennamansueto/lending-web-app-v2.git
cd lending-web-app-v2
docker compose up -d          # postgres:16 on localhost:5432, db/user/pw lending/lending/lending_pw_2014
docker exec -i lending-postgres psql -v ON_ERROR_STOP=1 -U lending -d lending \
  -f /database/postgres/schema.sql

# 3. Migrate and prove parity (exit 0 = zero mismatches).
dotnet build tools/migrator
dotnet run --project tools/migrator -- migrate
dotnet run --project tools/migrator -- verify

# 4. Idempotency: a second migrate leaves an identical, still-verified database.
dump() { docker exec -i lending-postgres pg_dump -U lending -d lending \
           --data-only --no-owner --column-inserts \
         | grep -v '^\\\(un\)\?restrict ' | sha256sum; }
dotnet run --project tools/migrator -- migrate && dotnet run --project tools/migrator -- verify && dump
dotnet run --project tools/migrator -- migrate && dotnet run --project tools/migrator -- verify && dump
# the two digests must be identical

# 5. Optional: the harness on its own, straight against the migrated database.
docker exec -i lending-postgres psql -v ON_ERROR_STOP=1 -x -U lending -d lending \
  -f /database/checks/checksum_postgres.sql
```

The `grep -v` above strips `pg_dump`'s per-invocation `\restrict` token, which is random
by design and is the only line that differs between two dumps of identical data.

Connection overrides: `MIGRATOR_ORACLE`, `MIGRATOR_POSTGRES`, `MIGRATOR_REPO_ROOT`
(see [`tools/migrator/README.md`](../tools/migrator/README.md)).
