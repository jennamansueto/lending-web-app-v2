-- =============================================================================
-- Reference Postgres implementation of the LENDING checksum spec (spec_version 1.0).
--
-- Run this against the migrated Postgres database and compare its output with
-- database/checks/baseline-oracle.json. Every row_count, numeric SUM and
-- row_hash_chain must match exactly (chains are uppercase hex SHA-256).
--
--   psql -v ON_ERROR_STOP=1 -f database/checks/checksum_postgres.sql <db>
--
-- Prerequisites (see README.md for the full rule set):
--   * server_encoding = UTF8, standard_conforming_strings = on (default),
--     lc_numeric decimal separator '.' (the masks below are locale-independent).
--   * Columns must carry the scales listed in docs/data-profile.md
--     (e.g. numeric(14,2)); to_char with a fixed-scale mask would otherwise
--     round or pad differently from Oracle.
--   * DATE columns migrated as timestamp(0) or date; both render identically
--     under 'YYYY-MM-DD HH24:MI:SS' as long as no time component was invented.
-- =============================================================================

\pset footer off

-- BORROWER ---------------------------------------------------------------------
WITH RECURSIVE r AS (
    SELECT row_number() OVER (ORDER BY borrower_id) AS rn,
           upper(encode(sha256(convert_to(
               coalesce(to_char(borrower_id,       'FM99999999999999999990'),      '\N') || '|' ||
               coalesce(legal_name,                                                '\N') || '|' ||
               coalesce(tax_id,                                                    '\N') || '|' ||
               coalesce(to_char(credit_score,      'FM99999999999999999990'),      '\N') || '|' ||
               coalesce(to_char(deposit_balance,   'FM99999999999999999990.00'),   '\N') || '|' ||
               coalesce(to_char(years_in_business, 'FM99999999999999999990'),      '\N') || '|' ||
               coalesce(to_char(created_at,        'YYYY-MM-DD HH24:MI:SS'),       '\N')
           , 'UTF8')), 'hex')) AS rh
      FROM borrower
), c AS (
    SELECT 0::bigint AS rn, repeat('0', 64) AS acc
    UNION ALL
    SELECT c.rn + 1, upper(encode(sha256(convert_to(c.acc || r.rh, 'UTF8')), 'hex'))
      FROM c JOIN r ON r.rn = c.rn + 1
)
SELECT 'BORROWER' AS "table",
       (SELECT count(*) FROM borrower) AS row_count,
       (SELECT to_char(sum(borrower_id),       'FM99999999999999999990')    FROM borrower) AS sum_borrower_id,
       (SELECT to_char(sum(credit_score),      'FM99999999999999999990')    FROM borrower) AS sum_credit_score,
       (SELECT to_char(sum(deposit_balance),   'FM99999999999999999990.00') FROM borrower) AS sum_deposit_balance,
       (SELECT to_char(sum(years_in_business), 'FM99999999999999999990')    FROM borrower) AS sum_years_in_business,
       (SELECT acc FROM c ORDER BY rn DESC LIMIT 1) AS row_hash_chain;

-- LOAN_APPLICATION -------------------------------------------------------------
WITH RECURSIVE r AS (
    SELECT row_number() OVER (ORDER BY app_id) AS rn,
           upper(encode(sha256(convert_to(
               coalesce(to_char(app_id,       'FM99999999999999999990'),    '\N') || '|' ||
               coalesce(to_char(borrower_id,  'FM99999999999999999990'),    '\N') || '|' ||
               coalesce(product_type,                                       '\N') || '|' ||
               coalesce(to_char(amount,       'FM99999999999999999990.00'), '\N') || '|' ||
               coalesce(to_char(term_months,  'FM99999999999999999990'),    '\N') || '|' ||
               coalesce(to_char(credit_score, 'FM99999999999999999990'),    '\N') || '|' ||
               coalesce(to_char(dti,          'FM99999999999999999990.0000'), '\N') || '|' ||
               coalesce(to_char(ltv,          'FM99999999999999999990.0000'), '\N') || '|' ||
               coalesce(status,                                             '\N') || '|' ||
               coalesce(to_char(created_at,   'YYYY-MM-DD HH24:MI:SS'),     '\N')
           , 'UTF8')), 'hex')) AS rh
      FROM loan_application
), c AS (
    SELECT 0::bigint AS rn, repeat('0', 64) AS acc
    UNION ALL
    SELECT c.rn + 1, upper(encode(sha256(convert_to(c.acc || r.rh, 'UTF8')), 'hex'))
      FROM c JOIN r ON r.rn = c.rn + 1
)
SELECT 'LOAN_APPLICATION' AS "table",
       (SELECT count(*) FROM loan_application) AS row_count,
       (SELECT to_char(sum(app_id),       'FM99999999999999999990')      FROM loan_application) AS sum_app_id,
       (SELECT to_char(sum(borrower_id),  'FM99999999999999999990')      FROM loan_application) AS sum_borrower_id,
       (SELECT to_char(sum(amount),       'FM99999999999999999990.00')   FROM loan_application) AS sum_amount,
       (SELECT to_char(sum(term_months),  'FM99999999999999999990')      FROM loan_application) AS sum_term_months,
       (SELECT to_char(sum(credit_score), 'FM99999999999999999990')      FROM loan_application) AS sum_credit_score,
       (SELECT to_char(sum(dti),          'FM99999999999999999990.0000') FROM loan_application) AS sum_dti,
       (SELECT to_char(sum(ltv),          'FM99999999999999999990.0000') FROM loan_application) AS sum_ltv,
       (SELECT acc FROM c ORDER BY rn DESC LIMIT 1) AS row_hash_chain;

-- LOAN -------------------------------------------------------------------------
WITH RECURSIVE r AS (
    SELECT row_number() OVER (ORDER BY loan_id) AS rn,
           upper(encode(sha256(convert_to(
               coalesce(to_char(loan_id,      'FM99999999999999999990'),     '\N') || '|' ||
               coalesce(to_char(app_id,       'FM99999999999999999990'),     '\N') || '|' ||
               coalesce(to_char(borrower_id,  'FM99999999999999999990'),     '\N') || '|' ||
               coalesce(product_type,                                        '\N') || '|' ||
               coalesce(to_char(principal,    'FM99999999999999999990.00'),  '\N') || '|' ||
               coalesce(to_char(annual_rate,  'FM99999999999999999990.000'), '\N') || '|' ||
               coalesce(to_char(term_months,  'FM99999999999999999990'),     '\N') || '|' ||
               coalesce(to_char(orig_fee,     'FM99999999999999999990.00'),  '\N') || '|' ||
               coalesce(to_char(funded_date,  'YYYY-MM-DD HH24:MI:SS'),      '\N') || '|' ||
               coalesce(status,                                              '\N')
           , 'UTF8')), 'hex')) AS rh
      FROM loan
), c AS (
    SELECT 0::bigint AS rn, repeat('0', 64) AS acc
    UNION ALL
    SELECT c.rn + 1, upper(encode(sha256(convert_to(c.acc || r.rh, 'UTF8')), 'hex'))
      FROM c JOIN r ON r.rn = c.rn + 1
)
SELECT 'LOAN' AS "table",
       (SELECT count(*) FROM loan) AS row_count,
       (SELECT to_char(sum(loan_id),      'FM99999999999999999990')     FROM loan) AS sum_loan_id,
       (SELECT to_char(sum(app_id),       'FM99999999999999999990')     FROM loan) AS sum_app_id,
       (SELECT to_char(sum(borrower_id),  'FM99999999999999999990')     FROM loan) AS sum_borrower_id,
       (SELECT to_char(sum(principal),    'FM99999999999999999990.00')  FROM loan) AS sum_principal,
       (SELECT to_char(sum(annual_rate),  'FM99999999999999999990.000') FROM loan) AS sum_annual_rate,
       (SELECT to_char(sum(term_months),  'FM99999999999999999990')     FROM loan) AS sum_term_months,
       (SELECT to_char(sum(orig_fee),     'FM99999999999999999990.00')  FROM loan) AS sum_orig_fee,
       (SELECT acc FROM c ORDER BY rn DESC LIMIT 1) AS row_hash_chain;

-- PAYMENT_SCHEDULE -------------------------------------------------------------
WITH RECURSIVE r AS (
    SELECT row_number() OVER (ORDER BY loan_id, period_no) AS rn,
           upper(encode(sha256(convert_to(
               coalesce(to_char(loan_id,       'FM99999999999999999990'),    '\N') || '|' ||
               coalesce(to_char(period_no,     'FM99999999999999999990'),    '\N') || '|' ||
               coalesce(to_char(due_date,      'YYYY-MM-DD HH24:MI:SS'),     '\N') || '|' ||
               coalesce(to_char(payment_amt,   'FM99999999999999999990.00'), '\N') || '|' ||
               coalesce(to_char(interest_amt,  'FM99999999999999999990.00'), '\N') || '|' ||
               coalesce(to_char(principal_amt, 'FM99999999999999999990.00'), '\N') || '|' ||
               coalesce(to_char(balance_after, 'FM99999999999999999990.00'), '\N')
           , 'UTF8')), 'hex')) AS rh
      FROM payment_schedule
), c AS (
    SELECT 0::bigint AS rn, repeat('0', 64) AS acc
    UNION ALL
    SELECT c.rn + 1, upper(encode(sha256(convert_to(c.acc || r.rh, 'UTF8')), 'hex'))
      FROM c JOIN r ON r.rn = c.rn + 1
)
SELECT 'PAYMENT_SCHEDULE' AS "table",
       (SELECT count(*) FROM payment_schedule) AS row_count,
       (SELECT to_char(sum(loan_id),       'FM99999999999999999990')    FROM payment_schedule) AS sum_loan_id,
       (SELECT to_char(sum(period_no),     'FM99999999999999999990')    FROM payment_schedule) AS sum_period_no,
       (SELECT to_char(sum(payment_amt),   'FM99999999999999999990.00') FROM payment_schedule) AS sum_payment_amt,
       (SELECT to_char(sum(interest_amt),  'FM99999999999999999990.00') FROM payment_schedule) AS sum_interest_amt,
       (SELECT to_char(sum(principal_amt), 'FM99999999999999999990.00') FROM payment_schedule) AS sum_principal_amt,
       (SELECT to_char(sum(balance_after), 'FM99999999999999999990.00') FROM payment_schedule) AS sum_balance_after,
       (SELECT acc FROM c ORDER BY rn DESC LIMIT 1) AS row_hash_chain;

-- PAYMENT ----------------------------------------------------------------------
WITH RECURSIVE r AS (
    SELECT row_number() OVER (ORDER BY payment_id) AS rn,
           upper(encode(sha256(convert_to(
               coalesce(to_char(payment_id, 'FM99999999999999999990'),    '\N') || '|' ||
               coalesce(to_char(loan_id,    'FM99999999999999999990'),    '\N') || '|' ||
               coalesce(to_char(period_no,  'FM99999999999999999990'),    '\N') || '|' ||
               coalesce(to_char(paid_date,  'YYYY-MM-DD HH24:MI:SS'),     '\N') || '|' ||
               coalesce(to_char(amount,     'FM99999999999999999990.00'), '\N') || '|' ||
               coalesce(to_char(days_late,  'FM99999999999999999990'),    '\N') || '|' ||
               coalesce(to_char(late_fee,   'FM99999999999999999990.00'), '\N')
           , 'UTF8')), 'hex')) AS rh
      FROM payment
), c AS (
    SELECT 0::bigint AS rn, repeat('0', 64) AS acc
    UNION ALL
    SELECT c.rn + 1, upper(encode(sha256(convert_to(c.acc || r.rh, 'UTF8')), 'hex'))
      FROM c JOIN r ON r.rn = c.rn + 1
)
SELECT 'PAYMENT' AS "table",
       (SELECT count(*) FROM payment) AS row_count,
       (SELECT to_char(sum(payment_id), 'FM99999999999999999990')    FROM payment) AS sum_payment_id,
       (SELECT to_char(sum(loan_id),    'FM99999999999999999990')    FROM payment) AS sum_loan_id,
       (SELECT to_char(sum(period_no),  'FM99999999999999999990')    FROM payment) AS sum_period_no,
       (SELECT to_char(sum(amount),     'FM99999999999999999990.00') FROM payment) AS sum_amount,
       (SELECT to_char(sum(days_late),  'FM99999999999999999990')    FROM payment) AS sum_days_late,
       (SELECT to_char(sum(late_fee),   'FM99999999999999999990.00') FROM payment) AS sum_late_fee,
       (SELECT acc FROM c ORDER BY rn DESC LIMIT 1) AS row_hash_chain;
