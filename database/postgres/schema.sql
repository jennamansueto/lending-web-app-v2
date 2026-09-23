-- Contoso Commercial Lending — Postgres system of record (Layer 1).
--
-- Translated from the legacy Oracle DDL (lending-desktop-app database/schema.sql)
-- and the Phase 1 data profile (lending-desktop-app docs/data-profile.md).
--
-- Rules for this file:
--   * legacy table and column names are kept verbatim, unquoted lower case;
--   * declarative constraints only — no procedural business logic lives in the
--     database (no rule functions, no triggers); all business rules belong to
--     the service layer;
--   * every constraint is explicitly named (the Oracle names are
--     system-generated and unstable).

BEGIN;

DROP TABLE IF EXISTS payment CASCADE;
DROP TABLE IF EXISTS payment_schedule CASCADE;
DROP TABLE IF EXISTS loan CASCADE;
DROP TABLE IF EXISTS loan_application CASCADE;
DROP TABLE IF EXISTS borrower CASCADE;
DROP SEQUENCE IF EXISTS seq_loan_application;
DROP SEQUENCE IF EXISTS seq_loan;
DROP SEQUENCE IF EXISTS seq_payment;

-- ---------------------------------------------------------------- borrower
CREATE TABLE borrower (
    borrower_id        integer        NOT NULL,
    legal_name         varchar(200)   NOT NULL,
    tax_id             varchar(20)    NOT NULL,
    credit_score       smallint       NOT NULL,
    deposit_balance    numeric(14,2)  NOT NULL DEFAULT 0,
    years_in_business  smallint       NOT NULL DEFAULT 0,
    created_at         timestamp(0)   NOT NULL DEFAULT localtimestamp(0),
    CONSTRAINT pk_borrower     PRIMARY KEY (borrower_id),
    CONSTRAINT uq_borrower_tax_id UNIQUE (tax_id)
);

-- -------------------------------------------------------- loan_application
CREATE TABLE loan_application (
    app_id        integer        NOT NULL,
    borrower_id   integer        NOT NULL,
    product_type  varchar(10)    NOT NULL,
    amount        numeric(14,2)  NOT NULL,
    term_months   smallint       NOT NULL,
    credit_score  smallint       NOT NULL,
    dti           numeric(6,4),
    ltv           numeric(6,4),
    status        varchar(20)    NOT NULL DEFAULT 'SUBMITTED',
    created_at    timestamp(0)   NOT NULL DEFAULT localtimestamp(0),
    CONSTRAINT pk_loan_application PRIMARY KEY (app_id),
    CONSTRAINT fk_loan_application_borrower FOREIGN KEY (borrower_id)
        REFERENCES borrower (borrower_id) ON DELETE NO ACTION ON UPDATE NO ACTION
        NOT DEFERRABLE,
    CONSTRAINT ck_loan_application_product_type
        CHECK (product_type IN ('TERM','LOC','EQUIP'))
);

-- -------------------------------------------------------------------- loan
-- No product_type CHECK here: the legacy schema constrains product_type on
-- loan_application only, and that asymmetry is preserved as-is.
CREATE TABLE loan (
    loan_id       integer        NOT NULL,
    app_id        integer,
    borrower_id   integer        NOT NULL,
    product_type  varchar(10)    NOT NULL,
    principal     numeric(14,2)  NOT NULL,
    annual_rate   numeric(6,3)   NOT NULL,
    term_months   smallint       NOT NULL,
    orig_fee      numeric(12,2)  NOT NULL,
    funded_date   timestamp(0)   NOT NULL,
    status        varchar(20)    NOT NULL DEFAULT 'ACTIVE',
    CONSTRAINT pk_loan PRIMARY KEY (loan_id),
    CONSTRAINT fk_loan_application FOREIGN KEY (app_id)
        REFERENCES loan_application (app_id) ON DELETE NO ACTION ON UPDATE NO ACTION
        NOT DEFERRABLE,
    CONSTRAINT fk_loan_borrower FOREIGN KEY (borrower_id)
        REFERENCES borrower (borrower_id) ON DELETE NO ACTION ON UPDATE NO ACTION
        NOT DEFERRABLE
);

-- -------------------------------------------------------- payment_schedule
CREATE TABLE payment_schedule (
    loan_id        integer        NOT NULL,
    period_no      smallint       NOT NULL,
    due_date       timestamp(0)   NOT NULL,
    payment_amt    numeric(12,2)  NOT NULL,
    interest_amt   numeric(12,2)  NOT NULL,
    principal_amt  numeric(12,2)  NOT NULL,
    balance_after  numeric(14,2)  NOT NULL,
    CONSTRAINT pk_payment_schedule PRIMARY KEY (loan_id, period_no),
    CONSTRAINT fk_payment_schedule_loan FOREIGN KEY (loan_id)
        REFERENCES loan (loan_id) ON DELETE NO ACTION ON UPDATE NO ACTION
        NOT DEFERRABLE
);

-- ----------------------------------------------------------------- payment
-- No FK on (loan_id, period_no) -> payment_schedule: the legacy schema has
-- none, so a payment may reference a period the schedule does not contain.
CREATE TABLE payment (
    payment_id   integer        NOT NULL,
    loan_id      integer        NOT NULL,
    period_no    smallint       NOT NULL,
    paid_date    timestamp(0)   NOT NULL,
    amount       numeric(12,2)  NOT NULL,
    days_late    smallint       NOT NULL DEFAULT 0,
    late_fee     numeric(10,2)  NOT NULL DEFAULT 0,
    CONSTRAINT pk_payment PRIMARY KEY (payment_id),
    CONSTRAINT fk_payment_loan FOREIGN KEY (loan_id)
        REFERENCES loan (loan_id) ON DELETE NO ACTION ON UPDATE NO ACTION
        NOT DEFERRABLE
);

-- --------------------------------------------------------------- sequences
-- Explicit START WITH values matching the Oracle sequences; not serial /
-- identity defaults derived from max(id).
CREATE SEQUENCE seq_loan_application START WITH 1000 INCREMENT BY 1 MINVALUE 1 NO CYCLE;
CREATE SEQUENCE seq_loan             START WITH 5000 INCREMENT BY 1 MINVALUE 1 NO CYCLE;
CREATE SEQUENCE seq_payment          START WITH 90000 INCREMENT BY 1 MINVALUE 1 NO CYCLE;

-- ----------------------------------------------------------------- indexes
-- The primary-key and unique indexes are created implicitly by the
-- constraints above; these mirror the legacy user-named indexes.
CREATE INDEX ix_app_borrower   ON loan_application (borrower_id);
CREATE INDEX ix_loan_borrower  ON loan (borrower_id);
CREATE INDEX ix_payment_loan   ON payment (loan_id);

COMMIT;
