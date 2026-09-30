using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.LightningManager.Controllers;

// Only generic, public PWA assets are anonymous. No store lookup or financial data here.
[AllowAnonymous]
[Route("stores/{storeId}/lightning/BTC/wallet")]
public sealed class LightningWalletAssetsController : Controller
{
    [HttpGet("manifest.webmanifest")]
    public IActionResult Manifest(string storeId)
    {
        var root = Url.Action(nameof(LightningWalletController.Home), "LightningWallet", new { storeId })!.TrimEnd('/') + "/";
        return Content(JsonConvert.SerializeObject(new
        {
            id = root, name = "Lightning Manager Wallet", short_name = "LN Wallet",
            start_url = root, scope = root, display = "standalone", background_color = "#141820", theme_color = "#141820",
            icons = new[] {
                new { src = root + "assets/icon-192.png", sizes = "192x192", type = "image/png", purpose = "any" },
                new { src = root + "assets/icon-512.png", sizes = "512x512", type = "image/png", purpose = "any" }
            }
        }), "application/manifest+json");
    }

    [HttpGet("worker.js")]
    public IActionResult Worker() => Asset("worker.js");

    [HttpGet("offline")]
    public IActionResult Offline() => Asset("offline.html");

    [HttpGet("assets/{name}")]
    public IActionResult Asset(string name)
    {
        var contentType = name switch
        {
            "wallet.js" or "worker.js" => "text/javascript",
            "wallet.css" => "text/css",
            "offline.html" => "text/html",
            "icon-192.png" or "icon-512.png" => "image/png",
            _ => null
        };
        if (contentType is null) return NotFound();
        var stream = typeof(LightningWalletAssetsController).Assembly.GetManifestResourceStream(
            $"BTCPayServer.Plugins.LightningManager.Wallet.Assets.{name}");
        if (stream is null) return NotFound();
        Response.Headers.CacheControl = "public, max-age=0, must-revalidate";
        return File(stream, contentType);
    }
}
