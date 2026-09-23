# Layer 1 — Data / System of Record

Postgres 16 replaces Oracle as the system of record. This layer holds **tables, keys, declarative
constraints, migration tooling and the checksum verifier — and nothing else**: no PL/pgSQL rule
functions, no triggers, no rate/fee/eligibility logic. Every business rule lives in Layer 2
(see [`architecture.md`](architecture.md)).

The legacy source is the Oracle `LENDING` schema in
[`lending-desktop-app`](https://github.com/jennamansueto/lending-desktop-app) (branch
`phase1-integration`), whose structure is profiled in that repo's `docs/data-profile.md` and whose
content is pinned by `database/checks/baseline.json`.

| Artifact | Purpose |
| --- | --- |
| `docker-compose.yml` | `lending-postgres` (Postgres 16, port 5432, db/user `lending`) |
| `database/postgres/schema.sql` | the DDL translated from the Oracle schema |
| `database/postgres/reset.sql` | truncate every table, sequences back to their start values |
| `database/setup-postgres.sh` | applies either script inside the container |
| `tools/migrator/` | .NET 8 CLI: `migrate` and `verify` |
| `parity/reports/data-parity.json` | machine-readable output of `verify` (generated, not committed) |

## Running it

```bash
# 1. legacy Oracle, in a clone of lending-desktop-app (branch phase1-integration)
docker compose up -d && ./database/setup-db.sh        # lending/lending_pw_2014@localhost:1521/FREEPDB1

# 2. Postgres, in this repo
docker compose up -d && ./database/setup-postgres.sh  # lending/lending_pw_2014@localhost:5432/lending

# 3. copy the data, then prove it is identical
dotnet run --project tools/migrator -- migrate
dotnet run --project tools/migrator -- verify         # exit 0 only if every check matches

# reset the demo to an empty database at any time
./database/setup-postgres.sh reset
```

Connection strings come from `ORACLE_CONN` and `POSTGRES_CONN`; both default to the local demo
values above, so the commands work with no environment set up.

`migrate` truncates all five target tables, copies every row from Oracle in foreign-key order via
`COPY ... FORMAT BINARY`, and then points each sequence at `max(id) + 1` (its declared start value
when the table is empty). It is re-runnable any number of times: each run starts from a truncate and
ends at the same rows and the same sequence values, so `verify` passes identically after the second
and every later run.

`verify` compares, per table, with **zero tolerance**: the row count, the `SUM()` of every numeric
column rendered at the column's declared scale, and the two checksums defined below. It prints a
per-table/per-column report, writes `parity/reports/data-parity.json`
(`{table, check, oracle, postgres, pass}` per entry) and exits non-zero if a single check fails.

## Type mapping

| Oracle | Postgres | Columns |
| --- | --- | --- |
| `NUMBER(14,2)` | `numeric(14,2)` | `deposit_balance`, `loan_application.amount`, `principal`, `balance_after` |
| `NUMBER(12,2)` | `numeric(12,2)` | `orig_fee`, `payment_amt`, `interest_amt`, `principal_amt`, `payment.amount` |
| `NUMBER(10,2)` | `numeric(10,2)` | `late_fee` |
| `NUMBER(6,3)` | `numeric(6,3)` | `annual_rate` |
| `NUMBER(6,4)` | `numeric(6,4)` | `dti`, `ltv` |
| `NUMBER(10,0)` | `integer` | every `*_id` column (max value today 90005 ≪ 2³¹) |
| `NUMBER(4,0)` | `smallint` | `credit_score`, `term_months`, `period_no`, `days_late` |
| `NUMBER(3,0)` | `smallint` | `years_in_business` |
| `VARCHAR2(n BYTE)` | `varchar(n)` | `legal_name`, `tax_id`, `product_type`, `status` |
| `DATE` | `timestamp(0)` (no time zone) | `created_at`, `funded_date`, `due_date`, `paid_date` |
| `DEFAULT SYSDATE` | `DEFAULT localtimestamp(0)` | both `created_at` columns |
| `CREATE SEQUENCE … START WITH n` | `CREATE SEQUENCE … START WITH n` | `seq_loan_application` 1000, `seq_loan` 5000, `seq_payment` 90000 |

Scale is always declared on the column: an unconstrained `numeric` would render `7.25` instead of
`7.250` and break the hash. Money is never `double precision`/`real`, and the migrator moves values
as .NET `decimal` end to end — never `double`.

Oracle `DATE` carries a time component, so it maps to `timestamp(0)`, never `date` (which would
silently truncate) and never `timestamptz` (which renders differently per session time zone).

## Checksum algorithm on Postgres

Restated from the legacy contract (`lending-desktop-app` `database/checks/README.md`,
`algorithm_version = 1`); `verify` reproduces it bit-for-bit and the migrated database yields the
same `table_hash` values as the committed Oracle baseline.

1. **Canonical row** — every column of the table in declared column order, joined with `chr(31)`
   (U+001F UNIT SEPARATOR); no leading, trailing or newline separator.
2. **NULL** — encoded as the two characters `\N`, never as an empty string, so Postgres' distinction
   between `''` and `NULL` cannot hide behind Oracle's conflation of the two. A `SUM()` over an
   all-NULL column is likewise `\N`.
3. **Numbers** — rendered at the column's declared scale with no grouping, no exponent, no padding
   and no leading `+`. On Postgres that is simply `column::text`, because the declared scale is kept
   on the column (`numeric(6,3)` → `7.250`). Sums use the same scale as the column.
4. **Dates** — `to_char(column, 'YYYY-MM-DD HH24:MI:SS')` on Postgres,
   `TO_CHAR(column, 'YYYY-MM-DD HH24:MI:SS')` on Oracle.
5. **Text** — emitted verbatim (no trimming, no case folding, no normalisation) and hashed as UTF-8;
   the database is created with `--encoding=UTF8 --locale=C`. Every `ORDER BY` uses numeric primary
   keys only, so collation cannot reorder rows.
6. **Hash and fold** — with rows in primary-key order:

   ```text
   row_hash_i   = lowercase_hex(SHA256(utf8(canonical_row_i)))
   fold_0       = 64 ASCII '0'
   fold_i       = lowercase_hex(SHA256(utf8(fold_{i-1} || row_hash_i)))   -- hex strings as ASCII
   table_hash   = fold_n
   row_hash_sum = Σ to_integer_base16(first 15 hex chars of row_hash_i)
   ```

   `row_hash_sum` is the order-independent cross-check: if `table_hash` differs while `row_hash_sum`
   matches, the rows are identical and only their order differs.

Each engine renders its own canonical rows and sums server-side (Oracle with `TO_CHAR`, Postgres
with `::text`/`to_char`); only the fold — a pure function of those strings — runs in the migrator.
A formatting difference between the engines therefore surfaces as a mismatch instead of being
normalised away by shared client-side formatting code.

## Deliberate differences from Oracle

| # | Difference | Justification |
| --- | --- | --- |
| 1 | `NUMBER(p,0)` becomes `integer`/`smallint` instead of `numeric(p,0)` | Recommended by the Phase 1 data profile §3.2 and required for the service layer to read ids as .NET `int`. The domains are wider, never narrower, so no value can fail to load; the rendered text (`742`) is identical, so checksums are unaffected. |
| 2 | Every constraint is explicitly named (`pk_borrower`, `fk_loan_borrower`, …) | All Oracle constraints except `PK_PAYMENT_SCHEDULE` carry system-generated names from an instance-wide counter; they are not stable identifiers and nothing may assert on them. |
| 3 | Identifiers are unquoted lower case | Oracle folds to upper case and Postgres to lower case; unquoted lower case keeps hand-written and ORM-generated SQL identical. Names themselves are unchanged. |
| 4 | `SYSDATE` defaults become `localtimestamp(0)` | Zone-less, second-precision server local time, matching Oracle `DATE`. `now()`/`current_timestamp` are `timestamptz` and carry sub-second precision. |
| 5 | No `CHECK` was added on `status`, `credit_score` or `loan.product_type` | Oracle constrains none of them (the `product_type` `CHECK` exists on `loan_application` only). Adding constraints would be a behaviour change requiring its own parity evidence; the asymmetry is preserved as-is. |
| 6 | `schema.sql` drops the objects it creates before creating them | Makes a demo re-initialisation idempotent. Oracle's script assumes a fresh schema created by `setup-db.sh`. |
| 7 | Sequences are plain sequences, not identity columns | The seed data uses literal ids that overlap the sequence ranges (`app_id` 1–7 vs start 1000); an inferred identity start would collide. `migrate` sets each sequence to `max(id) + 1`, which is free in every case. |
| 8 | Oracle's `LAST_NUMBER` is not replicated verbatim | It is the next value the *instance* will serve from its cache of 20 and can jump after a restart, so it is informational, not an equality check. The migrator instead guarantees the next id is free. |
| 9 | The database contains no procedural code | `PKG_LENDING` (late fee, payoff) is business logic and moves to Layer 2 by design; the data layer keeps declarative constraints only. |
| 10 | The missing index on `LOAN.APP_ID` is left missing | Reproduces the legacy physical design; adding it is a tuning decision, not part of a behaviour-preserving migration. |

Not a difference, but worth stating: the legacy data is migrated as-is. Nothing is cleaned,
deduplicated or repaired, and the legacy repository is never written to — the checksum baseline is
only ever read.
