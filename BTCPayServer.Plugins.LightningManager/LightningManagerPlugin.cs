using BTCPayServer;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.LightningManager.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.LightningManager;

public class LightningManagerPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    ];

    public override void Execute(IServiceCollection services)
    {
        var plugins = (PluginServiceCollection)services;

        plugins.AddSingleton<ILightningCapabilityService, LightningCapabilityService>();
        plugins.AddSingleton<ILightningLedgerRepository, LightningLedgerRepository>();
        plugins.AddSingleton<IStoreLightningLedgerService, StoreLightningLedgerService>();
        plugins.AddSingleton<IStoreLightningAccessService, StoreLightningAccessService>();
        plugins.AddScoped<IStoreLightningManagerContextFactory, StoreLightningManagerContextFactory>();
        plugins.AddSingleton<ILightningManagerService, LightningManagerService>();
        services.Configure<MvcOptions>(options => options.Filters.Add<LightningManagerStoreAccessFilter>());
        services.AddSingleton<IHostedService, LightningManagerLedgerInvoiceListener>();
        services.AddSingleton<IHostedService, LightningManagerStoreAccessGuard>();
        services.AddScheduledTask<LightningManagerLedgerReconciler>(TimeSpan.FromMinutes(1));
        services.AddMigration("20260509_lightningmanagerledger", """
            CREATE TABLE IF NOT EXISTS "LightningManagerLedgerAccounts" (
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

            CREATE TABLE IF NOT EXISTS "LightningManagerLedgerEntries" (
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

            CREATE TABLE IF NOT EXISTS "LightningManagerInvoicePaymentMethods" (
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

            ALTER TABLE "LightningManagerLedgerEntries"
                ADD COLUMN IF NOT EXISTS "PaymentAmountMSat" bigint NULL,
                ADD COLUMN IF NOT EXISTS "FeeLimitMSat" bigint NULL,
                ADD COLUMN IF NOT EXISTS "InvoiceId" text NULL,
                ADD COLUMN IF NOT EXISTS "PaymentHash" text NULL,
                ADD COLUMN IF NOT EXISTS "Preimage" text NULL,
                ADD COLUMN IF NOT EXISTS "Metadata" text NULL,
                ADD COLUMN IF NOT EXISTS "SettledAt" timestamp with time zone NULL;

            ALTER TABLE "LightningManagerInvoicePaymentMethods"
                ADD COLUMN IF NOT EXISTS "PaymentHash" text NOT NULL DEFAULT '',
                ADD COLUMN IF NOT EXISTS "VerificationStatus" text NOT NULL DEFAULT 'External',
                ADD COLUMN IF NOT EXISTS "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now();

            UPDATE "LightningManagerInvoicePaymentMethods"
                SET "PaymentHash" = ''
                WHERE "PaymentHash" IS NULL;

            ALTER TABLE "LightningManagerInvoicePaymentMethods"
                ALTER COLUMN "PaymentHash" SET DEFAULT '',
                ALTER COLUMN "PaymentHash" SET NOT NULL;

            ALTER TABLE "LightningManagerInvoicePaymentMethods"
                DROP CONSTRAINT IF EXISTS "PK_LightningManagerInvoicePaymentMethods";

            ALTER TABLE "LightningManagerInvoicePaymentMethods"
                ADD CONSTRAINT "PK_LightningManagerInvoicePaymentMethods"
                    PRIMARY KEY ("InvoiceId", "PaymentMethodId", "PaymentHash");

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_LightningManagerLedgerEntries_Idempotency"
                ON "LightningManagerLedgerEntries" ("StoreId", "CryptoCode", "IdempotencyKey");

            CREATE INDEX IF NOT EXISTS "IX_LightningManagerLedgerEntries_StoreCryptoCreated"
                ON "LightningManagerLedgerEntries" ("StoreId", "CryptoCode", "CreatedAt", "Id");

            CREATE INDEX IF NOT EXISTS "IX_LightningManagerLedgerEntries_PendingSends"
                ON "LightningManagerLedgerEntries" ("Type", "Status", "CreatedAt", "Id");

            CREATE INDEX IF NOT EXISTS "IX_LightningManagerLedgerEntries_SendAttempts"
                ON "LightningManagerLedgerEntries" ("StoreId", "CryptoCode", "PaymentHash", "Type", "Status")
                WHERE "PaymentHash" IS NOT NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_LightningManagerLedgerEntries_PendingSendPaymentHash"
                ON "LightningManagerLedgerEntries" ("CryptoCode", "PaymentHash")
                WHERE "Type" = 'ReserveSend' AND "Status" = 'Pending' AND "PaymentHash" IS NOT NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_LightningManagerLedgerEntries_SettledSendPaymentHash"
                ON "LightningManagerLedgerEntries" ("CryptoCode", "PaymentHash")
                WHERE "Type" = 'DebitSendAmount' AND "PaymentHash" IS NOT NULL;
            """);
        services.AddMigration("20260509_lightningmanagerledger_finalize_schema", """
            ALTER TABLE "LightningManagerLedgerAccounts"
                ADD COLUMN IF NOT EXISTS "Enabled" boolean NOT NULL DEFAULT false,
                ADD COLUMN IF NOT EXISTS "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                ADD COLUMN IF NOT EXISTS "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now();

            ALTER TABLE "LightningManagerLedgerEntries"
                ADD COLUMN IF NOT EXISTS "Type" text NOT NULL DEFAULT '',
                ADD COLUMN IF NOT EXISTS "Status" text NOT NULL DEFAULT 'Settled',
                ADD COLUMN IF NOT EXISTS "AmountMSat" bigint NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS "ReservedMSat" bigint NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS "PaymentAmountMSat" bigint NULL,
                ADD COLUMN IF NOT EXISTS "FeeLimitMSat" bigint NULL,
                ADD COLUMN IF NOT EXISTS "IdempotencyKey" text NOT NULL DEFAULT '',
                ADD COLUMN IF NOT EXISTS "InvoiceId" text NULL,
                ADD COLUMN IF NOT EXISTS "PaymentHash" text NULL,
                ADD COLUMN IF NOT EXISTS "Preimage" text NULL,
                ADD COLUMN IF NOT EXISTS "Description" text NULL,
                ADD COLUMN IF NOT EXISTS "Metadata" text NULL,
                ADD COLUMN IF NOT EXISTS "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                ADD COLUMN IF NOT EXISTS "SettledAt" timestamp with time zone NULL;

            ALTER TABLE "LightningManagerInvoicePaymentMethods"
                ADD COLUMN IF NOT EXISTS "PaymentHash" text NOT NULL DEFAULT '',
                ADD COLUMN IF NOT EXISTS "VerificationStatus" text NOT NULL DEFAULT 'External',
                ADD COLUMN IF NOT EXISTS "UpdatedAt" timestamp with time zone NOT NULL DEFAULT now();

            UPDATE "LightningManagerInvoicePaymentMethods"
                SET "PaymentHash" = ''
                WHERE "PaymentHash" IS NULL;

            ALTER TABLE "LightningManagerInvoicePaymentMethods"
                ALTER COLUMN "PaymentHash" SET DEFAULT '',
                ALTER COLUMN "PaymentHash" SET NOT NULL;

            ALTER TABLE "LightningManagerInvoicePaymentMethods"
                DROP CONSTRAINT IF EXISTS "PK_LightningManagerInvoicePaymentMethods";

            ALTER TABLE "LightningManagerInvoicePaymentMethods"
                ADD CONSTRAINT "PK_LightningManagerInvoicePaymentMethods"
                    PRIMARY KEY ("InvoiceId", "PaymentMethodId", "PaymentHash");

            DELETE FROM "LightningManagerLedgerEntries" e
                WHERE NOT EXISTS (
                    SELECT 1 FROM "Stores" s WHERE s."Id" = e."StoreId"
                );

            DELETE FROM "LightningManagerLedgerAccounts" a
                WHERE NOT EXISTS (
                    SELECT 1 FROM "Stores" s WHERE s."Id" = a."StoreId"
                );

            ALTER TABLE "LightningManagerLedgerAccounts"
                DROP CONSTRAINT IF EXISTS "FK_LightningManagerLedgerAccounts_Stores";

            ALTER TABLE "LightningManagerLedgerAccounts"
                ADD CONSTRAINT "FK_LightningManagerLedgerAccounts_Stores"
                    FOREIGN KEY ("StoreId")
                    REFERENCES "Stores" ("Id")
                    ON DELETE CASCADE;

            ALTER TABLE "LightningManagerLedgerEntries"
                DROP CONSTRAINT IF EXISTS "FK_LightningManagerLedgerEntries_Accounts";

            ALTER TABLE "LightningManagerLedgerEntries"
                ADD CONSTRAINT "FK_LightningManagerLedgerEntries_Accounts"
                    FOREIGN KEY ("StoreId", "CryptoCode")
                    REFERENCES "LightningManagerLedgerAccounts" ("StoreId", "CryptoCode")
                    ON DELETE CASCADE;

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_LightningManagerLedgerEntries_Idempotency"
                ON "LightningManagerLedgerEntries" ("StoreId", "CryptoCode", "IdempotencyKey");

            CREATE INDEX IF NOT EXISTS "IX_LightningManagerLedgerEntries_StoreCryptoCreated"
                ON "LightningManagerLedgerEntries" ("StoreId", "CryptoCode", "CreatedAt", "Id");

            CREATE INDEX IF NOT EXISTS "IX_LightningManagerLedgerEntries_PendingSends"
                ON "LightningManagerLedgerEntries" ("Type", "Status", "CreatedAt", "Id");

            CREATE INDEX IF NOT EXISTS "IX_LightningManagerLedgerEntries_SendAttempts"
                ON "LightningManagerLedgerEntries" ("StoreId", "CryptoCode", "PaymentHash", "Type", "Status")
                WHERE "PaymentHash" IS NOT NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_LightningManagerLedgerEntries_PendingSendPaymentHash"
                ON "LightningManagerLedgerEntries" ("CryptoCode", "PaymentHash")
                WHERE "Type" = 'ReserveSend' AND "Status" = 'Pending' AND "PaymentHash" IS NOT NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_LightningManagerLedgerEntries_SettledSendPaymentHash"
                ON "LightningManagerLedgerEntries" ("CryptoCode", "PaymentHash")
                WHERE "Type" = 'DebitSendAmount' AND "PaymentHash" IS NOT NULL;
            """);
        plugins.AddUIExtension("lightning-nav", "LightningManager/LightningManagerNav");
        plugins.AddUIExtension("server-nav", "LightningManager/ServerNav");
    }
}
