using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.LightningManager.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using UpdatePaymentMethodRequest = BTCPayServer.Client.Models.UpdatePaymentMethodRequest;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerStoreAccessFilterTests
{
    [Fact]
    public async Task UiLightningSettings_WhenAccountDisabled_BlocksNativeEnable()
    {
        var filter = new LightningManagerStoreAccessFilter(
            CreateHandlers(),
            new StaticLedgerRepository(new LightningLedgerAccount
            {
                StoreId = "store-1",
                CryptoCode = "BTC",
                Enabled = false
            }));
        var context = CreateContext(
            HttpMethods.Post,
            "/stores/store-1/lightning/BTC/settings",
            new Dictionary<string, object?>
            {
                ["vm"] = new LightningSettingsViewModel
                {
                    StoreId = "store-1",
                    CryptoCode = "BTC",
                    Enabled = true
                }
            });

        var executed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(
                ToActionContext(context),
                [],
                new object()));
        });

        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.False(executed);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Contains("disabled", result.Content);
    }

    [Fact]
    public async Task GreenfieldPaymentMethodUpdate_WhenAccountDisabled_BlocksNativeEnable()
    {
        var filter = new LightningManagerStoreAccessFilter(
            CreateHandlers(),
            new StaticLedgerRepository(new LightningLedgerAccount
            {
                StoreId = "store-1",
                CryptoCode = "BTC",
                Enabled = false
            }));
        var context = CreateContext(
            HttpMethods.Put,
            "/api/v1/stores/store-1/payment-methods/BTC-LN",
            new Dictionary<string, object?>
            {
                ["paymentMethodId"] = PaymentTypes.LN.GetPaymentMethodId("BTC"),
                ["request"] = new UpdatePaymentMethodRequest { Enabled = true }
            });

        var executed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(
                ToActionContext(context),
                [],
                new object()));
        });

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.False(executed);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task GreenfieldPaymentMethodUpdate_WhenIncomingConfigSwitchesToInternalAndAccountDisabled_BlocksNativeEnable()
    {
        var handlers = CreateHandlers();
        var config = new LightningPaymentMethodConfig();
        config.SetInternalNode();
        var filter = new LightningManagerStoreAccessFilter(
            handlers,
            new StaticLedgerRepository(new LightningLedgerAccount
            {
                StoreId = "store-1",
                CryptoCode = "BTC",
                Enabled = false
            }));
        var context = CreateContext(
            HttpMethods.Put,
            "/api/v1/stores/store-1/payment-methods/BTC-LN",
            new Dictionary<string, object?>
            {
                ["storeId"] = "store-1",
                ["paymentMethodId"] = PaymentTypes.LN.GetPaymentMethodId("BTC"),
                ["request"] = new UpdatePaymentMethodRequest
                {
                    Enabled = true,
                    Config = JToken.FromObject(config)
                }
            },
            new StoreData { Id = "store-1", StoreName = "Store 1" });

        var executed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(
                ToActionContext(context),
                [],
                new object()));
        });

        Assert.IsType<ObjectResult>(context.Result);
        Assert.False(executed);
    }

    [Fact]
    public async Task GreenfieldPaymentMethodUpdate_WhenIncomingInternalConfigOmitsEnabledAndAccountDisabled_BlocksNativeEnable()
    {
        var handlers = CreateHandlers();
        var config = new LightningPaymentMethodConfig();
        config.SetInternalNode();
        var filter = new LightningManagerStoreAccessFilter(
            handlers,
            new StaticLedgerRepository(new LightningLedgerAccount
            {
                StoreId = "store-1",
                CryptoCode = "BTC",
                Enabled = false
            }));
        var context = CreateContext(
            HttpMethods.Put,
            "/api/v1/stores/store-1/payment-methods/BTC-LN",
            new Dictionary<string, object?>
            {
                ["storeId"] = "store-1",
                ["paymentMethodId"] = PaymentTypes.LN.GetPaymentMethodId("BTC"),
                ["request"] = new UpdatePaymentMethodRequest
                {
                    Config = JToken.FromObject(config)
                }
            },
            CreateStoreWithExternalLightning(handlers));

        var executed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(
                ToActionContext(context),
                [],
                new object()));
        });

        Assert.IsType<ObjectResult>(context.Result);
        Assert.False(executed);
    }

    [Fact]
    public async Task GreenfieldPaymentMethodUpdate_WhenIncomingInternalConfigAndAccountMissing_BlocksNativeEnable()
    {
        var handlers = CreateHandlers();
        var config = new LightningPaymentMethodConfig();
        config.SetInternalNode();
        var filter = new LightningManagerStoreAccessFilter(
            handlers,
            new StaticLedgerRepository(null));
        var context = CreateContext(
            HttpMethods.Put,
            "/api/v1/stores/store-1/payment-methods/BTC-LN",
            new Dictionary<string, object?>
            {
                ["storeId"] = "store-1",
                ["paymentMethodId"] = PaymentTypes.LN.GetPaymentMethodId("BTC"),
                ["request"] = new UpdatePaymentMethodRequest
                {
                    Config = JToken.FromObject(config)
                }
            },
            CreateStoreWithExternalLightning(handlers));

        var executed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(
                ToActionContext(context),
                [],
                new object()));
        });

        Assert.IsType<ObjectResult>(context.Result);
        Assert.False(executed);
    }

    [Fact]
    public async Task GreenfieldPaymentMethodUpdate_WhenIncomingExternalConfigAndAccountDisabled_AllowsNativeEnable()
    {
        var handlers = CreateHandlers();
        var filter = new LightningManagerStoreAccessFilter(
            handlers,
            new StaticLedgerRepository(new LightningLedgerAccount
            {
                StoreId = "store-1",
                CryptoCode = "BTC",
                Enabled = false
            }));
        var context = CreateContext(
            HttpMethods.Put,
            "/api/v1/stores/store-1/payment-methods/BTC-LN",
            new Dictionary<string, object?>
            {
                ["storeId"] = "store-1",
                ["paymentMethodId"] = PaymentTypes.LN.GetPaymentMethodId("BTC"),
                ["request"] = new UpdatePaymentMethodRequest
                {
                    Enabled = true,
                    Config = JToken.FromObject(new LightningPaymentMethodConfig
                    {
                        ConnectionString = "type=clightning;server=http://127.0.0.1:9835/"
                    })
                }
            });

        var executed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(
                ToActionContext(context),
                [],
                new object()));
        });

        Assert.Null(context.Result);
        Assert.True(executed);
    }

    [Fact]
    public async Task GreenfieldPaymentMethodUpdate_WithNonBtcLightning_AllowsNativeEnable()
    {
        var handlers = CreateHandlers();
        var config = new LightningPaymentMethodConfig();
        config.SetInternalNode();
        var filter = new LightningManagerStoreAccessFilter(
            handlers,
            new StaticLedgerRepository(new LightningLedgerAccount
            {
                StoreId = "store-1",
                CryptoCode = "LTC",
                Enabled = false
            }));
        var context = CreateContext(
            HttpMethods.Put,
            "/api/v1/stores/store-1/payment-methods/LTC-LN",
            new Dictionary<string, object?>
            {
                ["storeId"] = "store-1",
                ["paymentMethodId"] = PaymentTypes.LN.GetPaymentMethodId("LTC"),
                ["request"] = new UpdatePaymentMethodRequest
                {
                    Enabled = true,
                    Config = JToken.FromObject(config)
                }
            },
            CreateStoreWithInternalLightning(handlers, "LTC"));

        var executed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(
                ToActionContext(context),
                [],
                new object()));
        });

        Assert.Null(context.Result);
        Assert.True(executed);
    }

    [Fact]
    public async Task GreenfieldLightningNodeApi_WhenInternalAccountDisabled_BlocksAccess()
    {
        var filter = new LightningManagerStoreAccessFilter(
            CreateHandlers(),
            new StaticLedgerRepository(new LightningLedgerAccount
            {
                StoreId = "store-1",
                CryptoCode = "BTC",
                Enabled = false
            }));
        var context = CreateContext(
            HttpMethods.Get,
            "/api/v1/stores/store-1/lightning/BTC/invoices",
            new Dictionary<string, object?>
            {
                ["cryptoCode"] = "BTC"
            });

        var executed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(
                ToActionContext(context),
                [],
                new object()));
        });

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.False(executed);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task GreenfieldLightningNodeApi_WhenInternalAccountMissing_BlocksAccess()
    {
        var filter = new LightningManagerStoreAccessFilter(
            CreateHandlers(),
            new StaticLedgerRepository(null));
        var context = CreateContext(
            HttpMethods.Get,
            "/api/v1/stores/store-1/lightning/BTC/invoices",
            new Dictionary<string, object?>
            {
                ["cryptoCode"] = "BTC"
            });

        var executed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(
                ToActionContext(context),
                [],
                new object()));
        });

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.False(executed);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task GreenfieldLightningNodeApi_WhenStoreUsesExternalNode_AllowsAccess()
    {
        var handlers = CreateHandlers();
        var filter = new LightningManagerStoreAccessFilter(
            handlers,
            new StaticLedgerRepository(new LightningLedgerAccount
            {
                StoreId = "store-1",
                CryptoCode = "BTC",
                Enabled = false
            }));
        var context = CreateContext(
            HttpMethods.Get,
            "/api/v1/stores/store-1/lightning/BTC/invoices",
            new Dictionary<string, object?>
            {
                ["cryptoCode"] = "BTC"
            },
            CreateStoreWithExternalLightning(handlers));

        var executed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(
                ToActionContext(context),
                [],
                new object()));
        });

        Assert.Null(context.Result);
        Assert.True(executed);
    }

    [Fact]
    public async Task UiLightningSetup_WhenAccountDisabled_BlocksInternalNodeCommand()
    {
        var filter = new LightningManagerStoreAccessFilter(
            CreateHandlers(),
            new StaticLedgerRepository(new LightningLedgerAccount
            {
                StoreId = "store-1",
                CryptoCode = "BTC",
                Enabled = false
            }));
        var context = CreateContext(
            HttpMethods.Post,
            "/stores/store-1/lightning/BTC/setup",
            new Dictionary<string, object?>
            {
                ["storeId"] = "store-1",
                ["cryptoCode"] = "BTC",
                ["vm"] = new LightningNodeViewModel
                {
                    LightningNodeType = LightningNodeType.Internal
                },
                ["command"] = "test"
            });

        var executed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(
                ToActionContext(context),
                [],
                new object()));
        });

        Assert.IsType<ContentResult>(context.Result);
        Assert.False(executed);
    }

    [Fact]
    public async Task UiLightningSetup_WhenAccountMissing_BlocksInternalNodeCommand()
    {
        var filter = new LightningManagerStoreAccessFilter(
            CreateHandlers(),
            new StaticLedgerRepository(null));
        var context = CreateContext(
            HttpMethods.Post,
            "/stores/store-1/lightning/BTC/setup",
            new Dictionary<string, object?>
            {
                ["storeId"] = "store-1",
                ["cryptoCode"] = "BTC",
                ["vm"] = new LightningNodeViewModel
                {
                    LightningNodeType = LightningNodeType.Internal
                },
                ["command"] = "save"
            });

        var executed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(
                ToActionContext(context),
                [],
                new object()));
        });

        Assert.IsType<ContentResult>(context.Result);
        Assert.False(executed);
    }

    private static ActionExecutingContext CreateContext(
        string method,
        string path,
        Dictionary<string, object?> arguments,
        StoreData? store = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.Path = path;
        httpContext.SetStoreData(store ?? CreateStoreWithInternalLightning(CreateHandlers()));
        return new ActionExecutingContext(
            new ActionContext(
                httpContext,
                new RouteData(),
                new ActionDescriptor()),
            [],
            arguments,
            new object());
    }

    private static ActionContext ToActionContext(ActionExecutingContext context)
    {
        return new ActionContext(
            context.HttpContext,
            context.RouteData,
            context.ActionDescriptor,
            context.ModelState);
    }

    private static PaymentMethodHandlerDictionary CreateHandlers()
    {
        return new PaymentMethodHandlerDictionary(
        [
            CreateLightningHandler("BTC"),
            CreateLnurlHandler("BTC"),
            CreateLightningHandler("LTC"),
            CreateLnurlHandler("LTC")
        ]);
    }

    private static StoreData CreateStoreWithInternalLightning(
        PaymentMethodHandlerDictionary handlers,
        string cryptoCode = "BTC")
    {
        var store = new StoreData { Id = "store-1", StoreName = "Store 1" };
        var config = new LightningPaymentMethodConfig();
        config.SetInternalNode();
        store.SetPaymentMethodConfig(handlers[PaymentTypes.LN.GetPaymentMethodId(cryptoCode)], config);
        store.SetPaymentMethodConfig(
            handlers[PaymentTypes.LNURL.GetPaymentMethodId(cryptoCode)],
            new LNURLPaymentMethodConfig());
        return store;
    }

    private static StoreData CreateStoreWithExternalLightning(PaymentMethodHandlerDictionary handlers)
    {
        var store = new StoreData { Id = "store-1", StoreName = "Store 1" };
        store.SetPaymentMethodConfig(
            handlers[PaymentTypes.LN.GetPaymentMethodId("BTC")],
            new LightningPaymentMethodConfig
            {
                ConnectionString = "type=clightning;server=http://127.0.0.1:9835/"
            });
        return store;
    }

    private static TestPaymentMethodHandler CreateLightningHandler(string cryptoCode)
    {
        return new TestPaymentMethodHandler(
            PaymentTypes.LN.GetPaymentMethodId(cryptoCode),
            token => token.ToObject<LightningPaymentMethodConfig>()!);
    }

    private static TestPaymentMethodHandler CreateLnurlHandler(string cryptoCode)
    {
        return new TestPaymentMethodHandler(
            PaymentTypes.LNURL.GetPaymentMethodId(cryptoCode),
            token => token.ToObject<LNURLPaymentMethodConfig>()!);
    }

    private sealed class TestPaymentMethodHandler(
        PaymentMethodId paymentMethodId,
        Func<JToken, object> parsePaymentMethodConfig) : IPaymentMethodHandler
    {
        public PaymentMethodId PaymentMethodId { get; } = paymentMethodId;
        public JsonSerializer Serializer { get; } = JsonSerializer.CreateDefault();
        public Task ConfigurePrompt(PaymentMethodContext context) => Task.CompletedTask;
        public Task BeforeFetchingRates(PaymentMethodContext context) => Task.CompletedTask;
        public object ParsePaymentPromptDetails(JToken details) => new object();
        public object ParsePaymentMethodConfig(JToken config) => parsePaymentMethodConfig(config);
        public object ParsePaymentDetails(JToken details) => details.ToObject<LightningLikePaymentData>()!;
    }

    private sealed class StaticLedgerRepository(LightningLedgerAccount? account) : ILightningLedgerRepository
    {
        public Task<LightningLedgerAccount?> GetAccountAsync(string storeId, string cryptoCode, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(account);
        }

        public Task<LightningLedgerAccount> EnsureAccountAsync(string storeId, string cryptoCode, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LightningLedgerSnapshot> GetSnapshotAsync(string storeId, string cryptoCode, int limit = 50, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LightningLedgerEntryPage> GetEntriesAsync(string storeId, string cryptoCode, LightningLedgerEntryQuery query, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LightningLedgerAccountSnapshot>> GetStoreAccountSnapshotsAsync(int? limit = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<long> GetBitcoinLedgerTotalMSatAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RecordInvoicePaymentMethodAsync(string invoiceId, string paymentMethodId, string paymentHash, string storeId, string cryptoCode, string verificationStatus, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LightningManagerInvoicePaymentMethod?> GetInvoicePaymentMethodAsync(string invoiceId, string paymentMethodId, string paymentHash, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> InsertEntryAsync(LightningLedgerEntryInput input, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LightningLedgerOperationResult<LightningSendReservation>> ReserveSendAsync(string storeId, string cryptoCode, string paymentHash, string bolt11, long paymentAmountMSat, long feeLimitMSat, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> SettleSendReservationAsync(string reservationEntryId, long paymentAmountMSat, long feeMSat, string paymentHash, string? preimage, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ReleaseSendReservationAsync(string reservationEntryId, string reason, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<LightningLedgerEntry>> GetPendingSendReservationsAsync(int limit = 100, DateTimeOffset? createdAfter = null, string? idAfter = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
