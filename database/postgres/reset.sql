-- Resets the Postgres system of record to an empty, freshly initialised state:
-- every table truncated and every sequence back at its declared start value.
-- Structure is untouched, so a demo can be reset without re-running schema.sql.

BEGIN;

TRUNCATE TABLE payment, payment_schedule, loan, loan_application, borrower RESTART IDENTITY CASCADE;

ALTER SEQUENCE seq_loan_application RESTART WITH 1000;
ALTER SEQUENCE seq_loan             RESTART WITH 5000;
ALTER SEQUENCE seq_payment          RESTART WITH 90000;

COMMIT;
