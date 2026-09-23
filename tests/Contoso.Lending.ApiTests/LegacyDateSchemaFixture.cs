using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Contoso.Lending.ApiTests;

/// <summary>
/// Boots the API against a throwaway Postgres schema whose date columns use the layer-1 types:
/// `funded_date`, `due_date` and `paid_date` are `timestamp(0)`, not `date`. Reading them as
/// `DateOnly` throws, so these tables are what pins the read path.
///
/// Requires the local Postgres of `docker-compose.yml`; the connection string comes from
/// `POSTGRES_CONN` and otherwise defaults to the documented development one.
/// </summary>
public sealed class LegacyDateSchemaFixture : IAsyncLifetime
{
    private const string TestSchema = "api_date_regression";

    private static readonly string BaseConnectionString =
        Environment.GetEnvironmentVariable("POSTGRES_CONN")
        ?? "Host=localhost;Database=lending;Username=lending;Password=lending_pw_2014";

    private WebApplicationFactory<Program>? _factory;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await using (var connection = new NpgsqlConnection(BaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(Ddl, connection);
            await command.ExecuteNonQueryAsync();
        }

        Environment.SetEnvironmentVariable(
            "POSTGRES_CONN", BaseConnectionString + $";Search Path={TestSchema}");
        _factory = new WebApplicationFactory<Program>();
        Client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        Environment.SetEnvironmentVariable("POSTGRES_CONN", BaseConnectionString);

        await using var connection = new NpgsqlConnection(BaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {TestSchema} CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    // A two-period loan: small enough to assert every row verbatim, funded on the 31st so the
    // Oracle ADD_MONTHS clamping in the seeded due dates stays visible.
    private const string Ddl = $"""
        DROP SCHEMA IF EXISTS {TestSchema} CASCADE;
        CREATE SCHEMA {TestSchema};
        SET search_path TO {TestSchema};

        CREATE TABLE borrower (
            borrower_id integer PRIMARY KEY,
            legal_name varchar(200) NOT NULL,
            tax_id varchar(20) NOT NULL,
            credit_score smallint NOT NULL,
            deposit_balance numeric(14,2) NOT NULL,
            years_in_business smallint NOT NULL,
            created_at timestamp(0) NOT NULL);

        CREATE TABLE loan_application (
            app_id integer PRIMARY KEY,
            borrower_id integer NOT NULL,
            product_type varchar(10) NOT NULL,
            amount numeric(14,2) NOT NULL,
            term_months smallint NOT NULL,
            credit_score smallint NOT NULL,
            dti numeric(6,4),
            ltv numeric(6,4),
            status varchar(20) NOT NULL,
            created_at timestamp(0) NOT NULL);

        CREATE TABLE loan (
            loan_id integer PRIMARY KEY,
            app_id integer,
            borrower_id integer NOT NULL,
            product_type varchar(10) NOT NULL,
            principal numeric(14,2) NOT NULL,
            annual_rate numeric(6,3) NOT NULL,
            term_months smallint NOT NULL,
            orig_fee numeric(12,2) NOT NULL,
            funded_date timestamp(0) NOT NULL,
            status varchar(20) NOT NULL);

        CREATE TABLE payment_schedule (
            loan_id integer NOT NULL,
            period_no smallint NOT NULL,
            due_date timestamp(0) NOT NULL,
            payment_amt numeric(12,2) NOT NULL,
            interest_amt numeric(12,2) NOT NULL,
            principal_amt numeric(12,2) NOT NULL,
            balance_after numeric(14,2) NOT NULL,
            PRIMARY KEY (loan_id, period_no));

        CREATE TABLE payment (
            payment_id integer PRIMARY KEY,
            loan_id integer NOT NULL,
            period_no smallint NOT NULL,
            paid_date timestamp(0) NOT NULL,
            amount numeric(12,2) NOT NULL,
            days_late smallint NOT NULL,
            late_fee numeric(10,2) NOT NULL);

        CREATE SEQUENCE seq_loan_application START WITH 1000;
        CREATE SEQUENCE seq_loan START WITH 5000;
        CREATE SEQUENCE seq_payment START WITH 90000;

        INSERT INTO borrower VALUES
            (1, 'HARBOR POINT LOGISTICS LLC', '81-2233445', 742, 120000.00, 9, timestamp '2019-01-15 00:00:00');
        INSERT INTO loan VALUES
            (5001, NULL, 1, 'TERM', 10000.00, 6.000, 2, 100.00, timestamp '2024-01-31 00:00:00', 'ACTIVE');
        INSERT INTO payment_schedule VALUES
            (5001, 1, timestamp '2024-02-29 00:00:00', 5037.56, 50.00, 4987.56, 5012.44),
            (5001, 2, timestamp '2024-03-31 00:00:00', 5037.50, 25.06, 5012.44, 0.00);
        INSERT INTO payment VALUES
            (90001, 5001, 1, timestamp '2024-03-12 00:00:00', 5037.56, 12, 150.00);
        """;
}

[CollectionDefinition(Name)]
public sealed class LegacyDateSchemaCollection : ICollectionFixture<LegacyDateSchemaFixture>
{
    public const string Name = "legacy-date-schema";
}
