using BTCPayServer.Abstractions.Models;
using Fido2NetLib;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.LightningManager.E2ETests;

// Loaded only through DEBUG_PLUGINS in this test executable, never packaged with the plugin.
// Chromium's virtual authenticator has no vendor attestation metadata. Keep the real
// Fido2 verifier and avoid an unrelated remote MDS dependency in deterministic tests.
public sealed class WalletWebAuthnTestPlugin : BaseBTCPayServerPlugin
{
    public override void Execute(IServiceCollection services) => services.AddSingleton<IMetadataService>(new VirtualAuthenticatorMetadata());

    private sealed class VirtualAuthenticatorMetadata : IMetadataService
    {
        public Task<MetadataBLOBPayloadEntry?> GetEntryAsync(Guid aaguid, CancellationToken cancellationToken = default)
        {
            // device/fido/virtual_ctap2_device.cc: kDeviceAaguid (or a stripped none attestation).
            if (aaguid != Guid.Empty && aaguid != Guid.Parse("01020304-0506-0708-0102-030405060708"))
                throw new InvalidOperationException("This fixture accepts only Chromium virtual authenticators.");
            return Task.FromResult<MetadataBLOBPayloadEntry?>(null);
        }
        public bool ConformanceTesting() => false;
    }
}
