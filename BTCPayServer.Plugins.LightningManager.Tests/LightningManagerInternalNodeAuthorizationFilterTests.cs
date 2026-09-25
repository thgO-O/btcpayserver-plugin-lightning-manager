using System.Reflection;
using System.Security.Claims;
using BTCPayServer;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.LightningManager.Controllers;
using BTCPayServer.Plugins.LightningManager.Filters;
using BTCPayServer.Security;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace BTCPayServer.Plugins.LightningManager.Tests;

public class LightningManagerInternalNodeAuthorizationFilterTests
{
    [Theory]
    [InlineData("BTC")]
    [InlineData("btc")]
    [InlineData("bTc")]
    [InlineData(" BTC")]
    [InlineData("BTC ")]
    [InlineData(" BTC ")]
    public async Task InternalNode_WithServerPermission_ExecutesAction(string cryptoCode)
    {
        var authorizationService = new RecordingAuthorizationService(succeeds: true);
        var (filter, context) = CreateFilterContext(isInternalNode: true, authorizationService, cryptoCode);
        var nextCalled = false;

        await filter.OnActionExecutionAsync(context, Next);

        Assert.True(nextCalled);
        Assert.Null(context.Result);
        Assert.Equal(1, authorizationService.Calls);
        Assert.Null(authorizationService.Resource);
        var requirement = Assert.Single(authorizationService.Requirements);
        Assert.Equal(
            Policies.CanUseInternalLightningNode,
            Assert.IsType<PolicyRequirement>(requirement).Policy);
        return;

        Task<ActionExecutedContext> Next()
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                ToActionContext(context),
                [],
                context.Controller));
        }
    }

    [Theory]
    [InlineData("BTC")]
    [InlineData("btc")]
    [InlineData("bTc")]
    [InlineData(" BTC")]
    [InlineData("BTC ")]
    [InlineData(" BTC ")]
    public async Task InternalNode_WithoutServerPermission_ForbidsBeforeAction(string cryptoCode)
    {
        var authorizationService = new RecordingAuthorizationService(succeeds: false);
        var (filter, context) = CreateFilterContext(isInternalNode: true, authorizationService, cryptoCode);
        var nextCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                ToActionContext(context),
                [],
                context.Controller));
        });

        Assert.False(nextCalled);
        Assert.IsType<ForbidResult>(context.Result);
        Assert.Equal(1, authorizationService.Calls);
    }

    [Theory]
    [InlineData("BTC")]
    [InlineData("btc")]
    [InlineData("bTc")]
    [InlineData(" BTC")]
    [InlineData("BTC ")]
    [InlineData(" BTC ")]
    public async Task ExternalNode_DoesNotRequireInternalNodePermission(string cryptoCode)
    {
        var authorizationService = new RecordingAuthorizationService(succeeds: false);
        var (filter, context) = CreateFilterContext(isInternalNode: false, authorizationService, cryptoCode);
        var nextCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                ToActionContext(context),
                [],
                context.Controller));
        });

        Assert.True(nextCalled);
        Assert.Null(context.Result);
        Assert.Equal(0, authorizationService.Calls);
    }

    [Theory]
    [InlineData("LTC")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task UnsupportedCrypto_DoesNotUseBitcoinNodePermission(string? cryptoCode)
    {
        var authorizationService = new RecordingAuthorizationService(succeeds: false);
        var (filter, context) = CreateFilterContext(isInternalNode: true, authorizationService, cryptoCode);
        var nextCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(
                ToActionContext(context),
                [],
                context.Controller));
        });

        Assert.True(nextCalled);
        Assert.Null(context.Result);
        Assert.Equal(0, authorizationService.Calls);
    }

    [Fact]
    public void ControllerMetadata_RequiresStorePermissionAndInternalNodeFilterForEveryAction()
    {
        var controllerType = typeof(LightningManagerController);
        var authorize = Assert.Single(controllerType.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(Policies.CanUseLightningNodeInStore, authorize.Policy);
        Assert.Equal(AuthenticationSchemes.Cookie, authorize.AuthenticationSchemes);

        var serviceFilter = Assert.Single(controllerType.GetCustomAttributes<ServiceFilterAttribute>());
        Assert.Equal(
            typeof(LightningManagerInternalNodeAuthorizationFilter),
            serviceFilter.ServiceType);

        var actions = controllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttributes()
                .Any(attribute => attribute is HttpGetAttribute or HttpPostAttribute))
            .ToArray();
        Assert.Equal(10, actions.Length);
    }

    private static (
        LightningManagerInternalNodeAuthorizationFilter Filter,
        ActionExecutingContext Context) CreateFilterContext(
        bool isInternalNode,
        RecordingAuthorizationService authorizationService,
        string? cryptoCode)
    {
        var handler = new FakeLightningPaymentMethodHandler();
        var store = new StoreData { Id = "store-1" };
        var config = new LightningPaymentMethodConfig();
        if (isInternalNode)
        {
            config.SetInternalNode();
        }
        else
        {
            config.ConnectionString = "type=clightning;server=tcp://127.0.0.1:30993/";
        }
        store.SetPaymentMethodConfig(handler, config);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "user-1")],
                AuthenticationSchemes.Cookie))
        };
        httpContext.SetStoreData(store);
        var routeData = new RouteData();
        routeData.Values["cryptoCode"] = cryptoCode;
        var actionContext = new ActionContext(
            httpContext,
            routeData,
            new ActionDescriptor(),
            new ModelStateDictionary());
        var executingContext = new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            new object());

        return (
            new LightningManagerInternalNodeAuthorizationFilter(
                new PaymentMethodHandlerDictionary([handler]),
                authorizationService),
            executingContext);
    }

    private static ActionContext ToActionContext(ActionExecutingContext context)
    {
        return new ActionContext(
            context.HttpContext,
            context.RouteData,
            context.ActionDescriptor,
            context.ModelState);
    }

    private sealed class RecordingAuthorizationService(bool succeeds) : IAuthorizationService
    {
        public int Calls { get; private set; }
        public object? Resource { get; private set; }
        public IAuthorizationRequirement[] Requirements { get; private set; } = [];

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements)
        {
            Calls++;
            Resource = resource;
            Requirements = requirements.ToArray();
            return Task.FromResult(
                succeeds ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            string policyName)
        {
            throw new InvalidOperationException("Named policies are not expected.");
        }
    }
}
