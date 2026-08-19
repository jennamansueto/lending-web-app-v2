-- =============================================================================
-- Contoso Commercial Lending — Postgres 16 schema (Layer 1, system of record)
--
-- Behaviour-preserving port of the legacy Oracle LENDING schema
-- (lending-desktop-app/database/schema.sql), following the per-column
-- "Postgres recommendation" in lending-desktop-app/docs/data-profile.md.
--
--   * identifiers are the lower_snake_case of the legacy names, no renaming
--   * every money/rate/ratio column is numeric(p,s) with the legacy p and s
--     (never float/double — hazards H-03, H-08)
--   * Oracle DATE -> timestamp(0) (hazard H-05)
--   * VARCHAR2(n) -> varchar(n) (hazard H-06: byte vs character semantics)
--   * every PK, FK, UNIQUE, NOT NULL, CHECK, DEFAULT, index and sequence is
--     reproduced, and nothing is added: absent constraints are legacy rules
--     (BR-DAT-017, BR-DAT-026, BR-DAT-027) and must stay absent
--   * constraints carry explicit names because the Oracle SYS_Cnnnnnn names are
--     not stable across rebuilds (hazard H-04)
--
-- No procedural logic lives here. PKG_LENDING (CALC_LATE_FEE / GET_PAYOFF_AMOUNT,
-- BR-SVC-001/002) is deliberately NOT ported: it is business logic and belongs to
-- the .NET 8 service layer (see docs/architecture.md and docs/data-layer.md).
--
--   psql -v ON_ERROR_STOP=1 -f database/postgres/schema.sql <db>
-- =============================================================================

-- Idempotent for local dev: drop children first (all FKs are NO ACTION).
DROP TABLE IF EXISTS payment;
DROP TABLE IF EXISTS payment_schedule;
DROP TABLE IF EXISTS loan;
DROP TABLE IF EXISTS loan_application;
DROP TABLE IF EXISTS borrower;
DROP SEQUENCE IF EXISTS seq_loan_application;
DROP SEQUENCE IF EXISTS seq_loan;
DROP SEQUENCE IF EXISTS seq_payment;

-- borrower --------------------------------------------------------------------
-- Legacy: BORROWER. borrower_id is caller-assigned; there is no sequence and no
-- identity (BR-DAT-001).
CREATE TABLE borrower (
    borrower_id        numeric(10)    NOT NULL,
    legal_name         varchar(200)   NOT NULL,
    tax_id             varchar(20)    NOT NULL,
    credit_score       numeric(4)     NOT NULL,
    deposit_balance    numeric(14,2)  DEFAULT 0 NOT NULL,
    years_in_business  numeric(3)     DEFAULT 0 NOT NULL,
    created_at         timestamp(0)   DEFAULT localtimestamp(0) NOT NULL,
    CONSTRAINT pk_borrower PRIMARY KEY (borrower_id),
    CONSTRAINT uq_borrower_tax_id UNIQUE (tax_id)
);

-- loan_application ------------------------------------------------------------
-- product_type carries the only value CHECK in the legacy schema (BR-DAT-009).
-- status has a default but no CHECK (BR-DAT-010) and dti/ltv stay nullable
-- (BR-DAT-012).
CREATE TABLE loan_application (
    app_id        numeric(10)    NOT NULL,
    borrower_id   numeric(10)    NOT NULL,
    product_type  varchar(10)    NOT NULL,
    amount        numeric(14,2)  NOT NULL,
    term_months   numeric(4)     NOT NULL,
    credit_score  numeric(4)     NOT NULL,
    dti           numeric(6,4),
    ltv           numeric(6,4),
    status        varchar(20)    DEFAULT 'SUBMITTED' NOT NULL,
    created_at    timestamp(0)   DEFAULT localtimestamp(0) NOT NULL,
    CONSTRAINT pk_loan_application PRIMARY KEY (app_id),
    CONSTRAINT ck_loan_application_product_type
        CHECK (product_type IN ('TERM', 'LOC', 'EQUIP')),
    CONSTRAINT fk_loan_application_borrower
        FOREIGN KEY (borrower_id) REFERENCES borrower (borrower_id)
);

-- loan ------------------------------------------------------------------------
-- app_id is nullable and NOT unique (BR-DAT-014). product_type has no CHECK
-- here, asymmetrically with loan_application (BR-DAT-017) — do not add one.
CREATE TABLE loan (
    loan_id       numeric(10)    NOT NULL,
    app_id        numeric(10),
    borrower_id   numeric(10)    NOT NULL,
    product_type  varchar(10)    NOT NULL,
    principal     numeric(14,2)  NOT NULL,
    annual_rate   numeric(6,3)   NOT NULL,
    term_months   numeric(4)     NOT NULL,
    orig_fee      numeric(12,2)  NOT NULL,
    funded_date   timestamp(0)   NOT NULL,
    status        varchar(20)    DEFAULT 'ACTIVE' NOT NULL,
    CONSTRAINT pk_loan PRIMARY KEY (loan_id),
    CONSTRAINT fk_loan_application
        FOREIGN KEY (app_id) REFERENCES loan_application (app_id),
    CONSTRAINT fk_loan_borrower
        FOREIGN KEY (borrower_id) REFERENCES borrower (borrower_id)
);

-- payment_schedule ------------------------------------------------------------
-- Composite PK (loan_id, period_no) — the only user-named constraint in the
-- legacy schema (BR-DAT-018). No CHECK ties payment_amt to
-- interest_amt + principal_amt: that identity holds in the data but is not
-- enforced, so it is not enforced here either.
CREATE TABLE payment_schedule (
    loan_id        numeric(10)    NOT NULL,
    period_no      numeric(4)     NOT NULL,
    due_date       timestamp(0)   NOT NULL,
    payment_amt    numeric(12,2)  NOT NULL,
    interest_amt   numeric(12,2)  NOT NULL,
    principal_amt  numeric(12,2)  NOT NULL,
    balance_after  numeric(14,2)  NOT NULL,
    CONSTRAINT pk_payment_schedule PRIMARY KEY (loan_id, period_no),
    CONSTRAINT fk_payment_schedule_loan
        FOREIGN KEY (loan_id) REFERENCES loan (loan_id)
);

-- payment ---------------------------------------------------------------------
-- period_no is not a FK to payment_schedule and (loan_id, period_no) is not
-- unique, so several payments per period are permitted (BR-DAT-021).
-- late_fee is stored, never derived — the $150 cap (BR-SVC-001) is code-side
-- only and must not become a CHECK.
CREATE TABLE payment (
    payment_id   numeric(10)    NOT NULL,
    loan_id      numeric(10)    NOT NULL,
    period_no    numeric(4)     NOT NULL,
    paid_date    timestamp(0)   NOT NULL,
    amount       numeric(12,2)  NOT NULL,
    days_late    numeric(4)     DEFAULT 0 NOT NULL,
    late_fee     numeric(10,2)  DEFAULT 0 NOT NULL,
    CONSTRAINT pk_payment PRIMARY KEY (payment_id),
    CONSTRAINT fk_payment_loan
        FOREIGN KEY (loan_id) REFERENCES loan (loan_id)
);

-- Sequences -------------------------------------------------------------------
-- START WITH values and cache size carried over verbatim (BR-DAT-023). The
-- legacy LAST_NUMBER equals START WITH for all three (no NEXTVAL has ever been
-- consumed), so a freshly created sequence is already at the legacy position;
-- `migrator migrate` re-asserts it with setval(..., n, false).
--
-- HAZARD H-01, reproduced deliberately, NOT fixed: seq_payment is positioned at
-- 90000 while max(payment_id) = 90005, so the first nextval() collides with an
-- existing primary key — exactly as in Oracle. Resynchronising it would be a
-- behaviour change and needs a separate, approved decision.
CREATE SEQUENCE seq_loan_application START WITH 1000 INCREMENT BY 1 NO CYCLE CACHE 20;
CREATE SEQUENCE seq_loan             START WITH 5000 INCREMENT BY 1 NO CYCLE CACHE 20;
CREATE SEQUENCE seq_payment          START WITH 90000 INCREMENT BY 1 NO CYCLE CACHE 20;

-- Indexes ---------------------------------------------------------------------
-- The unique indexes behind the PKs and the borrower.tax_id UNIQUE constraint are
-- created implicitly by Postgres. These are the three explicit non-unique
-- FK-supporting B-tree indexes from the legacy DDL (BR-DAT-024).
CREATE INDEX ix_app_borrower  ON loan_application (borrower_id);
CREATE INDEX ix_loan_borrower ON loan (borrower_id);
CREATE INDEX ix_payment_loan  ON payment (loan_id);
