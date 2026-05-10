#nullable enable
using System.Data.Common;
using BTCPayServer.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BTCPayServer.Plugins.LightningManager.Services;

public interface ILightningLedgerRepository
{
    Task<LightningLedgerAccount?> GetAccountAsync(string storeId, string cryptoCode, CancellationToken cancellationToken = default);
    Task<LightningLedgerAccount> EnsureAccountAsync(string storeId, string cryptoCode, CancellationToken cancellationToken = default);
    Task<LightningLedgerSnapshot> GetSnapshotAsync(string storeId, string cryptoCode, int limit = 50, CancellationToken cancellationToken = default);
    Task<LightningLedgerEntryPage> GetEntriesAsync(
        string storeId,
        string cryptoCode,
        LightningLedgerEntryQuery query,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LightningLedgerAccountSnapshot>> GetStoreAccountSnapshotsAsync(int? limit = null, CancellationToken cancellationToken = default);
    Task<long> GetBitcoinLedgerTotalMSatAsync(CancellationToken cancellationToken = default);
    Task RecordInvoicePaymentMethodAsync(
        string invoiceId,
        string paymentMethodId,
        string paymentHash,
        string storeId,
        string cryptoCode,
        string verificationStatus,
        CancellationToken cancellationToken = default);
    Task<LightningManagerInvoicePaymentMethod?> GetInvoicePaymentMethodAsync(
        string invoiceId,
        string paymentMethodId,
        string paymentHash,
        CancellationToken cancellationToken = default);
    Task<bool> InsertEntryAsync(LightningLedgerEntryInput input, CancellationToken cancellationToken = default);
    Task<LightningLedgerOperationResult<LightningSendReservation>> ReserveSendAsync(
        string storeId,
        string cryptoCode,
        string paymentHash,
        string bolt11,
        long paymentAmountMSat,
        long feeLimitMSat,
        CancellationToken cancellationToken = default);
    Task<bool> SettleSendReservationAsync(
        string reservationEntryId,
        long paymentAmountMSat,
        long feeMSat,
        string paymentHash,
        string? preimage,
        CancellationToken cancellationToken = default);
    Task ReleaseSendReservationAsync(
        string reservationEntryId,
        string reason,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LightningLedgerEntry>> GetPendingSendReservationsAsync(
        int limit = 100,
        DateTimeOffset? createdAfter = null,
        string? idAfter = null,
        CancellationToken cancellationToken = default);
}

public class LightningLedgerRepository(ApplicationDbContextFactory dbContextFactory) : ILightningLedgerRepository
{
    private enum LedgerEntryConflictHandling
    {
        None,
        IdempotencyKey,
        Any
    }

    public async Task<LightningLedgerAccount?> GetAccountAsync(
        string storeId,
        string cryptoCode,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.Database.GetDbConnection().QuerySingleOrDefaultAsync<LightningLedgerAccount>(
            new CommandDefinition(
                """
                SELECT "StoreId", "CryptoCode", "Enabled", "CreatedAt", "UpdatedAt"
                FROM "LightningManagerLedgerAccounts"
                WHERE "StoreId" = @storeId AND "CryptoCode" = @cryptoCode
                """,
                new { storeId, cryptoCode },
                cancellationToken: cancellationToken));
    }

    public async Task<LightningLedgerAccount> EnsureAccountAsync(
        string storeId,
        string cryptoCode,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await EnsureAccountAsync(ctx.Database.GetDbConnection(), null, storeId, cryptoCode, cancellationToken);
    }

    public async Task<LightningLedgerSnapshot> GetSnapshotAsync(
        string storeId,
        string cryptoCode,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var conn = ctx.Database.GetDbConnection();
        var account = await conn.QuerySingleOrDefaultAsync<LightningLedgerAccount>(
            new CommandDefinition(
                """
                SELECT "StoreId", "CryptoCode", "Enabled", "CreatedAt", "UpdatedAt"
                FROM "LightningManagerLedgerAccounts"
                WHERE "StoreId" = @storeId AND "CryptoCode" = @cryptoCode
                """,
                new { storeId, cryptoCode },
                cancellationToken: cancellationToken));
        var balance = await QueryBalanceAsync(conn, null, storeId, cryptoCode, cancellationToken);
        var entries = (await conn.QueryAsync<LightningLedgerEntry>(
            new CommandDefinition(
                """
                SELECT "Id", "StoreId", "CryptoCode", "Type", "Status", "AmountMSat", "ReservedMSat",
                       "PaymentAmountMSat", "FeeLimitMSat", "IdempotencyKey", "InvoiceId", "PaymentHash",
                       "Preimage", "Description", "Metadata", "CreatedAt", "SettledAt"
                FROM "LightningManagerLedgerEntries"
                WHERE "StoreId" = @storeId AND "CryptoCode" = @cryptoCode
                ORDER BY "CreatedAt" DESC, "Id" DESC
                LIMIT @limit
                """,
                new { storeId, cryptoCode, limit },
                cancellationToken: cancellationToken))).ToList();

        return new LightningLedgerSnapshot
        {
            Account = account,
            Balance = balance,
            Entries = entries
        };
    }

    public async Task<LightningLedgerEntryPage> GetEntriesAsync(
        string storeId,
        string cryptoCode,
        LightningLedgerEntryQuery query,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var hasSearch = !string.IsNullOrWhiteSpace(query.SearchTerm);
        var hasType = !string.IsNullOrWhiteSpace(query.Type);
        var hasStatus = !string.IsNullOrWhiteSpace(query.Status);
        var searchPattern = hasSearch ? $"%{EscapeLikePattern(query.SearchTerm!.Trim())}%" : string.Empty;
        var args = new
        {
            storeId,
            cryptoCode,
            hasType,
            type = hasType ? query.Type : string.Empty,
            hasStatus,
            status = hasStatus ? query.Status : string.Empty,
            hasSearch,
            searchPattern,
            skip = Math.Max(0, query.Skip),
            count = Math.Max(1, query.Count)
        };
        var total = await ctx.Database.GetDbConnection().ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                SELECT COUNT(*)::int
                FROM "LightningManagerLedgerEntries"
                WHERE "StoreId" = @storeId
                  AND "CryptoCode" = @cryptoCode
                  AND (@hasType = false OR "Type" = @type)
                  AND (@hasStatus = false OR "Status" = @status)
                  AND (
                      @hasSearch = false
                      OR "InvoiceId" ILIKE @searchPattern ESCAPE '\'
                      OR "PaymentHash" ILIKE @searchPattern ESCAPE '\'
                      OR "Description" ILIKE @searchPattern ESCAPE '\'
                      OR "Metadata" ILIKE @searchPattern ESCAPE '\'
                      OR "IdempotencyKey" ILIKE @searchPattern ESCAPE '\'
                  )
                """,
                args,
                cancellationToken: cancellationToken));
        var rows = await ctx.Database.GetDbConnection().QueryAsync<LightningLedgerEntry>(
            new CommandDefinition(
                """
                SELECT "Id", "StoreId", "CryptoCode", "Type", "Status", "AmountMSat", "ReservedMSat",
                       "PaymentAmountMSat", "FeeLimitMSat", "IdempotencyKey", "InvoiceId", "PaymentHash",
                       "Preimage", "Description", "Metadata", "CreatedAt", "SettledAt"
                FROM "LightningManagerLedgerEntries"
                WHERE "StoreId" = @storeId
                  AND "CryptoCode" = @cryptoCode
                  AND (@hasType = false OR "Type" = @type)
                  AND (@hasStatus = false OR "Status" = @status)
                  AND (
                      @hasSearch = false
                      OR "InvoiceId" ILIKE @searchPattern ESCAPE '\'
                      OR "PaymentHash" ILIKE @searchPattern ESCAPE '\'
                      OR "Description" ILIKE @searchPattern ESCAPE '\'
                      OR "Metadata" ILIKE @searchPattern ESCAPE '\'
                      OR "IdempotencyKey" ILIKE @searchPattern ESCAPE '\'
                  )
                ORDER BY "CreatedAt" DESC, "Id" DESC
                OFFSET @skip
                LIMIT @count
                """,
                args,
                cancellationToken: cancellationToken));
        return new LightningLedgerEntryPage
        {
            Entries = rows.ToList(),
            Total = total
        };
    }

    public async Task<long> GetBitcoinLedgerTotalMSatAsync(CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.Database.GetDbConnection().ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                SELECT COALESCE(SUM(e."AmountMSat"), 0)::bigint
                FROM "LightningManagerLedgerEntries" e
                JOIN "Stores" s ON s."Id" = e."StoreId"
                WHERE e."CryptoCode" = @cryptoCode AND e."Status" <> @voidStatus
                """,
                new { cryptoCode = LightningManagerCrypto.Bitcoin, voidStatus = LightningLedgerEntryStatuses.Void },
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<LightningLedgerAccountSnapshot>> GetStoreAccountSnapshotsAsync(
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var rows = await ctx.Database.GetDbConnection().QueryAsync<LightningLedgerAccountSnapshot>(
            new CommandDefinition(
                """
                SELECT s."Id" AS "StoreId",
                       s."StoreName",
                       @cryptoCode AS "CryptoCode",
                       COALESCE(a."Enabled", false) AS "Enabled",
                       COALESCE(SUM(e."AmountMSat"), 0)::bigint AS "TotalMSat",
                       GREATEST(COALESCE(SUM(e."ReservedMSat"), 0), 0)::bigint AS "ReservedMSat"
                FROM "Stores" s
                LEFT JOIN "LightningManagerLedgerAccounts" a
                  ON a."StoreId" = s."Id"
                 AND a."CryptoCode" = @cryptoCode
                LEFT JOIN "LightningManagerLedgerEntries" e
                  ON e."StoreId" = s."Id"
                 AND e."CryptoCode" = @cryptoCode
                 AND e."Status" <> @voidStatus
                GROUP BY s."Id", s."StoreName", a."Enabled"
                ORDER BY COALESCE(s."StoreName", s."Id"), s."Id"
                LIMIT @limit
                """,
                new { cryptoCode = LightningManagerCrypto.Bitcoin, limit, voidStatus = LightningLedgerEntryStatuses.Void },
                cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task RecordInvoicePaymentMethodAsync(
        string invoiceId,
        string paymentMethodId,
        string paymentHash,
        string storeId,
        string cryptoCode,
        string verificationStatus,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        await ctx.Database.GetDbConnection().ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO "LightningManagerInvoicePaymentMethods"
                    ("InvoiceId", "PaymentMethodId", "PaymentHash", "StoreId", "CryptoCode", "IsInternalNode", "VerificationStatus", "CreatedAt", "UpdatedAt")
                VALUES
                    (@invoiceId, @paymentMethodId, @paymentHash, @storeId, @cryptoCode, @isInternalNode, @verificationStatus, @now, @now)
                ON CONFLICT ("InvoiceId", "PaymentMethodId", "PaymentHash") DO UPDATE
                SET "StoreId" = EXCLUDED."StoreId",
                    "CryptoCode" = EXCLUDED."CryptoCode",
                    "IsInternalNode" = "LightningManagerInvoicePaymentMethods"."IsInternalNode" OR EXCLUDED."IsInternalNode",
                    "VerificationStatus" = CASE
                        WHEN "LightningManagerInvoicePaymentMethods"."VerificationStatus" = @internalStatus
                             OR "LightningManagerInvoicePaymentMethods"."IsInternalNode" THEN @internalStatus
                        ELSE EXCLUDED."VerificationStatus"
                    END,
                    "UpdatedAt" = EXCLUDED."UpdatedAt"
                """,
                new
                {
                    invoiceId,
                    paymentMethodId,
                    paymentHash,
                    storeId,
                    cryptoCode,
                    verificationStatus,
                    internalStatus = LightningManagerInvoicePaymentVerificationStatuses.Internal,
                    isInternalNode = verificationStatus == LightningManagerInvoicePaymentVerificationStatuses.Internal,
                    now = DateTimeOffset.UtcNow
                },
                cancellationToken: cancellationToken));
    }

    public async Task<LightningManagerInvoicePaymentMethod?> GetInvoicePaymentMethodAsync(
        string invoiceId,
        string paymentMethodId,
        string paymentHash,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.Database.GetDbConnection().QuerySingleOrDefaultAsync<LightningManagerInvoicePaymentMethod>(
            new CommandDefinition(
                """
                SELECT "InvoiceId", "PaymentMethodId", "PaymentHash", "StoreId", "CryptoCode", "VerificationStatus", "IsInternalNode", "CreatedAt", "UpdatedAt"
                FROM "LightningManagerInvoicePaymentMethods"
                WHERE "InvoiceId" = @invoiceId
                  AND "PaymentMethodId" = @paymentMethodId
                  AND ("PaymentHash" = @paymentHash OR "PaymentHash" = @legacyPaymentHash)
                ORDER BY CASE WHEN "PaymentHash" = @paymentHash THEN 0 ELSE 1 END
                LIMIT 1
                """,
                new { invoiceId, paymentMethodId, paymentHash, legacyPaymentHash = string.Empty },
                cancellationToken: cancellationToken));
    }

    public async Task<bool> InsertEntryAsync(LightningLedgerEntryInput input, CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var affected = await InsertEntryAsync(
            ctx.Database.GetDbConnection(),
            null,
            input,
            cancellationToken,
            conflictHandling: LedgerEntryConflictHandling.IdempotencyKey);
        return affected == 1;
    }

    public async Task<LightningLedgerOperationResult<LightningSendReservation>> ReserveSendAsync(
        string storeId,
        string cryptoCode,
        string paymentHash,
        string bolt11,
        long paymentAmountMSat,
        long feeLimitMSat,
        CancellationToken cancellationToken = default)
    {
        if (paymentAmountMSat < 0)
        {
            return LightningLedgerOperationResult<LightningSendReservation>.Failure("Payment amount must be non-negative.");
        }

        if (feeLimitMSat < 0)
        {
            return LightningLedgerOperationResult<LightningSendReservation>.Failure("Fee limit must be non-negative.");
        }

        if (!TryAddMSat(paymentAmountMSat, feeLimitMSat, out var reservedMSat))
        {
            return LightningLedgerOperationResult<LightningSendReservation>.Failure("Reserved amount is too large.");
        }

        await using var ctx = dbContextFactory.CreateContext();
        await using var tx = await ctx.Database.BeginTransactionAsync(cancellationToken);
        var dbTx = tx.GetDbTransaction();
        var conn = ctx.Database.GetDbConnection();

        await EnsureAccountAsync(conn, dbTx, storeId, cryptoCode, cancellationToken);
        var account = await LockAccountAsync(conn, dbTx, storeId, cryptoCode, cancellationToken);
        if (account is null || !account.Enabled)
        {
            return LightningLedgerOperationResult<LightningSendReservation>.Failure("Lightning access is disabled for this store.");
        }

        if (await HasActiveOrSettledSendAttemptAsync(conn, dbTx, cryptoCode, paymentHash, cancellationToken))
        {
            return LightningLedgerOperationResult<LightningSendReservation>.Failure("This invoice already has a pending or settled ledger payment attempt.");
        }

        var balance = await QueryBalanceAsync(conn, dbTx, storeId, cryptoCode, cancellationToken);
        if (balance.AvailableMSat < reservedMSat)
        {
            return LightningLedgerOperationResult<LightningSendReservation>.Failure("Insufficient Pay balance.");
        }

        var entryId = Guid.NewGuid().ToString("N");
        var affected = await InsertEntryAsync(
            conn,
            dbTx,
            new LightningLedgerEntryInput
            {
                StoreId = storeId,
                CryptoCode = cryptoCode,
                Type = LightningLedgerEntryTypes.ReserveSend,
                Status = LightningLedgerEntryStatuses.Pending,
                ReservedMSat = reservedMSat,
                PaymentAmountMSat = paymentAmountMSat,
                FeeLimitMSat = feeLimitMSat,
                IdempotencyKey = SendIdempotencyKey(paymentHash, entryId, "reserve"),
                PaymentHash = paymentHash,
                Description = "Managed Lightning send reservation",
                Metadata = bolt11
            },
            cancellationToken,
            entryId,
            LedgerEntryConflictHandling.Any);

        if (affected != 1)
        {
            return LightningLedgerOperationResult<LightningSendReservation>.Failure("This invoice already has a ledger payment attempt.");
        }

        await tx.CommitAsync(cancellationToken);
        return LightningLedgerOperationResult<LightningSendReservation>.Success(new LightningSendReservation
        {
            EntryId = entryId,
            StoreId = storeId,
            CryptoCode = cryptoCode,
            PaymentHash = paymentHash,
            PaymentAmountMSat = paymentAmountMSat,
            FeeLimitMSat = feeLimitMSat,
            ReservedMSat = reservedMSat
        });
    }

    public async Task<bool> SettleSendReservationAsync(
        string reservationEntryId,
        long paymentAmountMSat,
        long feeMSat,
        string paymentHash,
        string? preimage,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        await using var tx = await ctx.Database.BeginTransactionAsync(cancellationToken);
        var dbTx = tx.GetDbTransaction();
        var conn = ctx.Database.GetDbConnection();
        var reserve = await LockPendingReserveAsync(conn, dbTx, reservationEntryId, cancellationToken);
        if (reserve is null)
        {
            return false;
        }

        if (!string.Equals(reserve.PaymentHash, paymentHash, StringComparison.OrdinalIgnoreCase) ||
            paymentAmountMSat < 0 ||
            feeMSat < 0 ||
            !TryAddMSat(paymentAmountMSat, feeMSat, out var settledMSat) ||
            settledMSat > reserve.ReservedMSat ||
            reserve.FeeLimitMSat is null ||
            feeMSat > reserve.FeeLimitMSat.Value)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        try
        {
            if (!await InsertRequiredEntryAsync(conn, dbTx, new LightningLedgerEntryInput
                {
                    StoreId = reserve.StoreId,
                    CryptoCode = reserve.CryptoCode,
                    Type = LightningLedgerEntryTypes.ReleaseReserve,
                    Status = LightningLedgerEntryStatuses.Settled,
                    ReservedMSat = -reserve.ReservedMSat,
                    IdempotencyKey = SendIdempotencyKey(paymentHash, reservationEntryId, "release"),
                    PaymentHash = paymentHash,
                    Description = "Release managed send reservation"
                }, cancellationToken) ||
                !await InsertRequiredEntryAsync(conn, dbTx, new LightningLedgerEntryInput
                {
                    StoreId = reserve.StoreId,
                    CryptoCode = reserve.CryptoCode,
                    Type = LightningLedgerEntryTypes.DebitSendAmount,
                    Status = LightningLedgerEntryStatuses.Settled,
                    AmountMSat = -paymentAmountMSat,
                    IdempotencyKey = SendIdempotencyKey(paymentHash, reservationEntryId, "amount"),
                    PaymentHash = paymentHash,
                    Preimage = preimage,
                    Description = "Managed Lightning send amount"
                }, cancellationToken))
            {
                return false;
            }

            if (feeMSat > 0 &&
                !await InsertRequiredEntryAsync(conn, dbTx, new LightningLedgerEntryInput
                {
                    StoreId = reserve.StoreId,
                    CryptoCode = reserve.CryptoCode,
                    Type = LightningLedgerEntryTypes.DebitSendFee,
                    Status = LightningLedgerEntryStatuses.Settled,
                    AmountMSat = -feeMSat,
                    IdempotencyKey = SendIdempotencyKey(paymentHash, reservationEntryId, "fee"),
                    PaymentHash = paymentHash,
                    Description = "Managed Lightning send fee"
                }, cancellationToken))
            {
                return false;
            }
        }
        catch (DbException)
        {
            return false;
        }

        await MarkReserveSettledAsync(conn, dbTx, reservationEntryId, now, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task ReleaseSendReservationAsync(
        string reservationEntryId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        await using var tx = await ctx.Database.BeginTransactionAsync(cancellationToken);
        var dbTx = tx.GetDbTransaction();
        var conn = ctx.Database.GetDbConnection();
        var reserve = await LockPendingReserveAsync(conn, dbTx, reservationEntryId, cancellationToken);
        if (reserve is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(reserve.PaymentHash))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        try
        {
            if (!await InsertRequiredEntryAsync(conn, dbTx, new LightningLedgerEntryInput
            {
                StoreId = reserve.StoreId,
                CryptoCode = reserve.CryptoCode,
                Type = LightningLedgerEntryTypes.ReleaseReserve,
                Status = LightningLedgerEntryStatuses.Settled,
                ReservedMSat = -reserve.ReservedMSat,
                IdempotencyKey = SendIdempotencyKey(reserve.PaymentHash, reservationEntryId, "release"),
                PaymentHash = reserve.PaymentHash,
                Description = reason
            }, cancellationToken))
            {
                return;
            }
        }
        catch (DbException)
        {
            return;
        }

        await MarkReserveSettledAsync(conn, dbTx, reservationEntryId, now, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LightningLedgerEntry>> GetPendingSendReservationsAsync(
        int limit = 100,
        DateTimeOffset? createdAfter = null,
        string? idAfter = null,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var rows = await ctx.Database.GetDbConnection().QueryAsync<LightningLedgerEntry>(
            new CommandDefinition(
                """
                SELECT "Id", "StoreId", "CryptoCode", "Type", "Status", "AmountMSat", "ReservedMSat",
                       "PaymentAmountMSat", "FeeLimitMSat", "IdempotencyKey", "InvoiceId", "PaymentHash",
                       "Preimage", "Description", "Metadata", "CreatedAt", "SettledAt"
                FROM "LightningManagerLedgerEntries"
                WHERE "Type" = @type AND "Status" = @status
                  AND (
                      @createdAfter IS NULL
                      OR "CreatedAt" > @createdAfter
                      OR ("CreatedAt" = @createdAfter AND "Id" > @idAfter)
                  )
                ORDER BY "CreatedAt", "Id"
                LIMIT @limit
                """,
                new
                {
                    type = LightningLedgerEntryTypes.ReserveSend,
                    status = LightningLedgerEntryStatuses.Pending,
                    createdAfter,
                    idAfter,
                    limit
                },
                cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private static async Task<bool> HasActiveOrSettledSendAttemptAsync(
        DbConnection conn,
        DbTransaction tx,
        string cryptoCode,
        string paymentHash,
        CancellationToken cancellationToken)
    {
        return await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM "LightningManagerLedgerEntries"
                    WHERE "CryptoCode" = @cryptoCode
                      AND "PaymentHash" = @paymentHash
                      AND (
                          ("Type" = @reserveType AND "Status" = @pendingStatus)
                          OR "Type" = @debitType
                      )
                )
                """,
                new
                {
                    cryptoCode,
                    paymentHash,
                    reserveType = LightningLedgerEntryTypes.ReserveSend,
                    pendingStatus = LightningLedgerEntryStatuses.Pending,
                    debitType = LightningLedgerEntryTypes.DebitSendAmount
                },
                transaction: tx,
                cancellationToken: cancellationToken));
    }

    private static async Task<LightningLedgerBalance> QueryBalanceAsync(
        DbConnection conn,
        DbTransaction? tx,
        string storeId,
        string cryptoCode,
        CancellationToken cancellationToken)
    {
        return await conn.QuerySingleAsync<LightningLedgerBalance>(
            new CommandDefinition(
                """
                SELECT COALESCE(SUM("AmountMSat"), 0)::bigint AS "TotalMSat",
                       GREATEST(COALESCE(SUM("ReservedMSat"), 0), 0)::bigint AS "ReservedMSat"
                FROM "LightningManagerLedgerEntries"
                WHERE "StoreId" = @storeId
                  AND "CryptoCode" = @cryptoCode
                  AND "Status" <> @voidStatus
                """,
                new { storeId, cryptoCode, voidStatus = LightningLedgerEntryStatuses.Void },
                transaction: tx,
                cancellationToken: cancellationToken));
    }

    private static async Task<int> InsertEntryAsync(
        DbConnection conn,
        DbTransaction? tx,
        LightningLedgerEntryInput input,
        CancellationToken cancellationToken,
        string? entryId = null,
        LedgerEntryConflictHandling conflictHandling = LedgerEntryConflictHandling.None)
    {
        var conflictClause = conflictHandling switch
        {
            LedgerEntryConflictHandling.Any => "ON CONFLICT DO NOTHING",
            LedgerEntryConflictHandling.IdempotencyKey => """ON CONFLICT ("StoreId", "CryptoCode", "IdempotencyKey") DO NOTHING""",
            _ => string.Empty
        };
        return await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                INSERT INTO "LightningManagerLedgerEntries"
                    ("Id", "StoreId", "CryptoCode", "Type", "Status", "AmountMSat", "ReservedMSat",
                     "PaymentAmountMSat", "FeeLimitMSat", "IdempotencyKey", "InvoiceId", "PaymentHash",
                     "Preimage", "Description", "Metadata", "CreatedAt", "SettledAt")
                VALUES
                    (@id, @storeId, @cryptoCode, @type, @status, @amountMSat, @reservedMSat,
                     @paymentAmountMSat, @feeLimitMSat, @idempotencyKey, @invoiceId, @paymentHash,
                     @preimage, @description, @metadata, @createdAt, @settledAt)
                {conflictClause}
                """,
                new
                {
                    id = entryId ?? Guid.NewGuid().ToString("N"),
                    storeId = input.StoreId,
                    cryptoCode = input.CryptoCode,
                    type = input.Type,
                    status = input.Status,
                    amountMSat = input.AmountMSat,
                    reservedMSat = input.ReservedMSat,
                    paymentAmountMSat = input.PaymentAmountMSat,
                    feeLimitMSat = input.FeeLimitMSat,
                    idempotencyKey = input.IdempotencyKey,
                    invoiceId = input.InvoiceId,
                    paymentHash = input.PaymentHash,
                    preimage = input.Preimage,
                    description = input.Description,
                    metadata = input.Metadata,
                    createdAt = DateTimeOffset.UtcNow,
                    settledAt = input.Status == LightningLedgerEntryStatuses.Pending ? (DateTimeOffset?)null : DateTimeOffset.UtcNow
                },
                transaction: tx,
                cancellationToken: cancellationToken));
    }

    private static async Task<bool> InsertRequiredEntryAsync(
        DbConnection conn,
        DbTransaction tx,
        LightningLedgerEntryInput input,
        CancellationToken cancellationToken)
    {
        return await InsertEntryAsync(conn, tx, input, cancellationToken) == 1;
    }

    private static async Task<LightningLedgerAccount> EnsureAccountAsync(
        DbConnection conn,
        DbTransaction? tx,
        string storeId,
        string cryptoCode,
        CancellationToken cancellationToken)
    {
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO "LightningManagerLedgerAccounts" ("StoreId", "CryptoCode", "Enabled", "CreatedAt", "UpdatedAt")
                VALUES (@storeId, @cryptoCode, false, @now, @now)
                ON CONFLICT ("StoreId", "CryptoCode") DO NOTHING
                """,
                new { storeId, cryptoCode, now = DateTimeOffset.UtcNow },
                transaction: tx,
                cancellationToken: cancellationToken));

        return await conn.QuerySingleAsync<LightningLedgerAccount>(
            new CommandDefinition(
                """
                SELECT "StoreId", "CryptoCode", "Enabled", "CreatedAt", "UpdatedAt"
                FROM "LightningManagerLedgerAccounts"
                WHERE "StoreId" = @storeId AND "CryptoCode" = @cryptoCode
                """,
                new { storeId, cryptoCode },
                transaction: tx,
                cancellationToken: cancellationToken));
    }

    private static async Task<LightningLedgerAccount?> LockAccountAsync(
        DbConnection conn,
        DbTransaction tx,
        string storeId,
        string cryptoCode,
        CancellationToken cancellationToken)
    {
        return await conn.QuerySingleOrDefaultAsync<LightningLedgerAccount>(
            new CommandDefinition(
                """
                SELECT "StoreId", "CryptoCode", "Enabled", "CreatedAt", "UpdatedAt"
                FROM "LightningManagerLedgerAccounts"
                WHERE "StoreId" = @storeId AND "CryptoCode" = @cryptoCode
                FOR UPDATE
                """,
                new { storeId, cryptoCode },
                transaction: tx,
                cancellationToken: cancellationToken));
    }

    private static async Task<LightningLedgerEntry?> LockPendingReserveAsync(
        DbConnection conn,
        DbTransaction tx,
        string reservationEntryId,
        CancellationToken cancellationToken)
    {
        return await conn.QuerySingleOrDefaultAsync<LightningLedgerEntry>(
            new CommandDefinition(
                """
                SELECT "Id", "StoreId", "CryptoCode", "Type", "Status", "AmountMSat", "ReservedMSat",
                       "PaymentAmountMSat", "FeeLimitMSat", "IdempotencyKey", "InvoiceId", "PaymentHash",
                       "Preimage", "Description", "Metadata", "CreatedAt", "SettledAt"
                FROM "LightningManagerLedgerEntries"
                WHERE "Id" = @reservationEntryId
                  AND "Type" = @type
                  AND "Status" = @status
                FOR UPDATE
                """,
                new
                {
                    reservationEntryId,
                    type = LightningLedgerEntryTypes.ReserveSend,
                    status = LightningLedgerEntryStatuses.Pending
                },
                transaction: tx,
                cancellationToken: cancellationToken));
    }

    private static async Task MarkReserveSettledAsync(
        DbConnection conn,
        DbTransaction tx,
        string reservationEntryId,
        DateTimeOffset settledAt,
        CancellationToken cancellationToken)
    {
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE "LightningManagerLedgerEntries"
                SET "Status" = @status, "SettledAt" = @settledAt
                WHERE "Id" = @reservationEntryId
                """,
                new
                {
                    reservationEntryId,
                    status = LightningLedgerEntryStatuses.Settled,
                    settledAt
                },
                transaction: tx,
                cancellationToken: cancellationToken));
    }

    private static bool TryAddMSat(long left, long right, out long result)
    {
        try
        {
            result = checked(left + right);
            return true;
        }
        catch (OverflowException)
        {
            result = 0;
            return false;
        }
    }

    private static string SendIdempotencyKey(string paymentHash, string reservationEntryId, string suffix)
    {
        return $"send:{paymentHash}:{reservationEntryId}:{suffix}";
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }
}
