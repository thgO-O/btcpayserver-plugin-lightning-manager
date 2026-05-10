using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.LightningManager.Services;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningLedgerRepositoryConcurrencyTests
{
    private const string DefaultBtcpayTestPostgres =
        "User ID=postgres;Include Error Detail=true;Host=127.0.0.1;Port=39372;Database=btcpayserver";

    [Fact]
    public async Task ReserveSendAsync_WithConcurrentRequests_SerializesAvailableBalance()
    {
        var database = await CreatePostgresDatabaseAsync();

        try
        {
            var repository = CreateRepository(database.ConnectionString);
            await CreateLedgerSchemaAsync(database.ConnectionString);
            await InsertStoreAsync(database.ConnectionString, "store-1");
            await repository.EnsureAccountAsync("store-1", "BTC");
            await SetAccountEnabledAsync(database.ConnectionString, "store-1", "BTC", enabled: true);
            await repository.InsertEntryAsync(new LightningLedgerEntryInput
            {
                StoreId = "store-1",
                CryptoCode = "BTC",
                Type = LightningLedgerEntryTypes.CreditInvoicePayment,
                AmountMSat = 100_000,
                IdempotencyKey = "credit"
            });

            var attempts = Enumerable.Range(0, 5)
                .Select(i => repository.ReserveSendAsync(
                    "store-1",
                    "BTC",
                    $"payment-hash-{i}",
                    $"lnbcrt1test{i}",
                    paymentAmountMSat: 70_000,
                    feeLimitMSat: 0))
                .ToArray();

            var results = await Task.WhenAll(attempts);
            var snapshot = await repository.GetSnapshotAsync("store-1", "BTC");

            Assert.Equal(1, results.Count(result => result.IsSuccess));
            Assert.Equal(70_000, snapshot.Balance.ReservedMSat);
            Assert.Equal(30_000, snapshot.Balance.AvailableMSat);
        }
        finally
        {
            await DropPostgresDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task ReserveSendAsync_WithNegativeInputs_FailsWithoutReservation()
    {
        var database = await CreatePostgresDatabaseAsync();

        try
        {
            var repository = CreateRepository(database.ConnectionString);
            await CreateLedgerSchemaAsync(database.ConnectionString);
            await InsertStoreAsync(database.ConnectionString, "store-1");
            await repository.EnsureAccountAsync("store-1", "BTC");
            await SetAccountEnabledAsync(database.ConnectionString, "store-1", "BTC", enabled: true);
            await repository.InsertEntryAsync(new LightningLedgerEntryInput
            {
                StoreId = "store-1",
                CryptoCode = "BTC",
                Type = LightningLedgerEntryTypes.CreditInvoicePayment,
                AmountMSat = 200_000,
                IdempotencyKey = "credit"
            });

            var negativeFee = await repository.ReserveSendAsync(
                "store-1",
                "BTC",
                "payment-hash-fee",
                "lnbcrt1test",
                paymentAmountMSat: 100_000,
                feeLimitMSat: -1);
            var negativeAmount = await repository.ReserveSendAsync(
                "store-1",
                "BTC",
                "payment-hash-amount",
                "lnbcrt1test",
                paymentAmountMSat: -1,
                feeLimitMSat: 10_000);

            var snapshot = await repository.GetSnapshotAsync("store-1", "BTC", limit: 10);
            Assert.False(negativeFee.IsSuccess);
            Assert.Equal("Fee limit must be non-negative.", negativeFee.ErrorMessage);
            Assert.False(negativeAmount.IsSuccess);
            Assert.Equal("Payment amount must be non-negative.", negativeAmount.ErrorMessage);
            Assert.DoesNotContain(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.ReserveSend);
        }
        finally
        {
            await DropPostgresDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task SettleSendReservationAsync_WhenPaymentExceedsReserve_FailsWithoutSettlementEntries()
    {
        var database = await CreatePostgresDatabaseAsync();

        try
        {
            var repository = CreateRepository(database.ConnectionString);
            await CreateLedgerSchemaAsync(database.ConnectionString);
            await InsertStoreAsync(database.ConnectionString, "store-1");
            await repository.EnsureAccountAsync("store-1", "BTC");
            await SetAccountEnabledAsync(database.ConnectionString, "store-1", "BTC", enabled: true);
            await repository.InsertEntryAsync(new LightningLedgerEntryInput
            {
                StoreId = "store-1",
                CryptoCode = "BTC",
                Type = LightningLedgerEntryTypes.CreditInvoicePayment,
                AmountMSat = 200_000,
                IdempotencyKey = "credit"
            });
            var reservation = await repository.ReserveSendAsync(
                "store-1",
                "BTC",
                "payment-hash",
                "lnbcrt1test",
                paymentAmountMSat: 100_000,
                feeLimitMSat: 10_000);
            Assert.True(reservation.IsSuccess);

            var settled = await repository.SettleSendReservationAsync(
                reservation.Value!.EntryId,
                paymentAmountMSat: 100_000,
                feeMSat: 20_000,
                paymentHash: "payment-hash",
                preimage: "preimage");

            var snapshot = await repository.GetSnapshotAsync("store-1", "BTC", limit: 10);
            Assert.False(settled);
            Assert.Equal(200_000, snapshot.Balance.TotalMSat);
            Assert.Equal(110_000, snapshot.Balance.ReservedMSat);
            Assert.Contains(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.ReserveSend && e.Status == LightningLedgerEntryStatuses.Pending);
            Assert.DoesNotContain(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.ReleaseReserve);
            Assert.DoesNotContain(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.DebitSendAmount);
            Assert.DoesNotContain(snapshot.Entries, e => e.Type == LightningLedgerEntryTypes.DebitSendFee);
        }
        finally
        {
            await DropPostgresDatabaseAsync(database);
        }
    }

    private static LightningLedgerRepository CreateRepository(string connectionString)
    {
        return new LightningLedgerRepository(new ApplicationDbContextFactory(
            Options.Create(new DatabaseOptions
            {
                ConnectionString = connectionString
            }),
            NullLoggerFactory.Instance));
    }

    private static async Task<TestDatabase> CreatePostgresDatabaseAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("TESTS_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString))
            baseConnectionString = DefaultBtcpayTestPostgres;

        var dbName = $"lmtest_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = "postgres",
            Timeout = 2,
            CommandTimeout = 2
        };

        try
        {
            await using var admin = new NpgsqlConnection(adminBuilder.ToString());
            await admin.OpenAsync();
            await admin.ExecuteAsync($"""CREATE DATABASE "{dbName}";""");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not create the Postgres test database. Start the BTCPayServer.Tests stack or set TESTS_POSTGRES.",
                ex);
        }

        var testBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = dbName,
            Timeout = 5,
            CommandTimeout = 5
        };
        return new TestDatabase(adminBuilder.ToString(), testBuilder.ToString(), dbName);
    }

    private static async Task DropPostgresDatabaseAsync(TestDatabase database)
    {
        try
        {
            await using var admin = new NpgsqlConnection(database.AdminConnectionString);
            await admin.OpenAsync();
            await admin.ExecuteAsync(
                """
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = @databaseName
                  AND pid <> pg_backend_pid();
                """,
                new { databaseName = database.DatabaseName });
            await admin.ExecuteAsync($"""DROP DATABASE IF EXISTS "{database.DatabaseName}";""");
        }
        catch
        {
        }
    }

    private static async Task CreateLedgerSchemaAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.ExecuteAsync(
            """
            CREATE TABLE "Stores" (
                "Id" text NOT NULL,
                "StoreName" text NULL,
                CONSTRAINT "PK_Stores" PRIMARY KEY ("Id")
            );

            CREATE TABLE "LightningManagerLedgerAccounts" (
                "StoreId" text NOT NULL,
                "CryptoCode" text NOT NULL,
                "Enabled" boolean NOT NULL DEFAULT false,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                CONSTRAINT "PK_LightningManagerLedgerAccounts" PRIMARY KEY ("StoreId", "CryptoCode"),
                CONSTRAINT "FK_LightningManagerLedgerAccounts_Stores"
                    FOREIGN KEY ("StoreId")
                    REFERENCES "Stores" ("Id")
                    ON DELETE CASCADE
            );

            CREATE TABLE "LightningManagerLedgerEntries" (
                "Id" text NOT NULL,
                "StoreId" text NOT NULL,
                "CryptoCode" text NOT NULL,
                "Type" text NOT NULL,
                "Status" text NOT NULL,
                "AmountMSat" bigint NOT NULL DEFAULT 0,
                "ReservedMSat" bigint NOT NULL DEFAULT 0,
                "PaymentAmountMSat" bigint NULL,
                "FeeLimitMSat" bigint NULL,
                "IdempotencyKey" text NOT NULL,
                "InvoiceId" text NULL,
                "PaymentHash" text NULL,
                "Preimage" text NULL,
                "Description" text NULL,
                "Metadata" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "SettledAt" timestamp with time zone NULL,
                CONSTRAINT "PK_LightningManagerLedgerEntries" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_LightningManagerLedgerEntries_Accounts"
                    FOREIGN KEY ("StoreId", "CryptoCode")
                    REFERENCES "LightningManagerLedgerAccounts" ("StoreId", "CryptoCode")
                    ON DELETE CASCADE
            );

            CREATE TABLE "LightningManagerInvoicePaymentMethods" (
                "InvoiceId" text NOT NULL,
                "PaymentMethodId" text NOT NULL,
                "PaymentHash" text NOT NULL,
                "StoreId" text NOT NULL,
                "CryptoCode" text NOT NULL,
                "IsInternalNode" boolean NOT NULL,
                "VerificationStatus" text NOT NULL DEFAULT 'External',
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                CONSTRAINT "PK_LightningManagerInvoicePaymentMethods" PRIMARY KEY ("InvoiceId", "PaymentMethodId", "PaymentHash")
            );

            CREATE UNIQUE INDEX "IX_LightningManagerLedgerEntries_Idempotency"
                ON "LightningManagerLedgerEntries" ("StoreId", "CryptoCode", "IdempotencyKey");

            CREATE INDEX "IX_LightningManagerLedgerEntries_StoreCryptoCreated"
                ON "LightningManagerLedgerEntries" ("StoreId", "CryptoCode", "CreatedAt", "Id");

            CREATE INDEX "IX_LightningManagerLedgerEntries_PendingSends"
                ON "LightningManagerLedgerEntries" ("Type", "Status", "CreatedAt", "Id");

            CREATE INDEX "IX_LightningManagerLedgerEntries_SendAttempts"
                ON "LightningManagerLedgerEntries" ("StoreId", "CryptoCode", "PaymentHash", "Type", "Status")
                WHERE "PaymentHash" IS NOT NULL;

            CREATE UNIQUE INDEX "IX_LightningManagerLedgerEntries_PendingSendPaymentHash"
                ON "LightningManagerLedgerEntries" ("CryptoCode", "PaymentHash")
                WHERE "Type" = 'ReserveSend' AND "Status" = 'Pending' AND "PaymentHash" IS NOT NULL;

            CREATE UNIQUE INDEX "IX_LightningManagerLedgerEntries_SettledSendPaymentHash"
                ON "LightningManagerLedgerEntries" ("CryptoCode", "PaymentHash")
                WHERE "Type" = 'DebitSendAmount' AND "PaymentHash" IS NOT NULL;

            CREATE OR REPLACE FUNCTION "LightningManagerTestDelayReserveInsert"()
            RETURNS trigger AS $$
            BEGIN
                IF NEW."Type" = 'ReserveSend' THEN
                    PERFORM pg_sleep(0.2);
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER "LightningManagerTestDelayReserveInsert"
            BEFORE INSERT ON "LightningManagerLedgerEntries"
            FOR EACH ROW
            EXECUTE FUNCTION "LightningManagerTestDelayReserveInsert"();
            """);
    }

    private static async Task InsertStoreAsync(string connectionString, string storeId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.ExecuteAsync(
            """
            INSERT INTO "Stores" ("Id", "StoreName")
            VALUES (@storeId, @storeId)
            ON CONFLICT ("Id") DO NOTHING
            """,
            new { storeId });
    }

    private static async Task SetAccountEnabledAsync(
        string connectionString,
        string storeId,
        string cryptoCode,
        bool enabled)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.ExecuteAsync(
            """
            UPDATE "LightningManagerLedgerAccounts"
            SET "Enabled" = @enabled
            WHERE "StoreId" = @storeId AND "CryptoCode" = @cryptoCode
            """,
            new { storeId, cryptoCode, enabled });
    }

    private sealed record TestDatabase(
        string AdminConnectionString,
        string ConnectionString,
        string DatabaseName);
}
