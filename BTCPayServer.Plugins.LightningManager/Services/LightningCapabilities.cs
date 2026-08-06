namespace BTCPayServer.Plugins.LightningManager.Services;

public sealed class LightningCapabilities
{
    public static LightningCapabilities None { get; } = new();
    public static LightningCapabilities Full { get; } = new()
    {
        CanGetInfo = true,
        CanGetBalance = true,
        CanPayBolt11 = true,
        CanPayAmountless = true,
        CanSetMaxFee = true,
        CanConnectPeer = true,
        CanOpenChannel = true,
        CanListChannels = true
    };
    public static LightningCapabilities Phoenixd { get; } = new()
    {
        CanGetInfo = true,
        CanGetBalance = true,
        CanPayBolt11 = true,
        CanPayAmountless = true
    };
    public static LightningCapabilities BlinkBitcoin { get; } = new()
    {
        CanGetBalance = true,
        CanPayBolt11 = true
    };
    public static LightningCapabilities BlinkPayOnly { get; } = new()
    {
        CanPayBolt11 = true
    };

    private bool _canPayAmountless;

    public bool CanGetInfo { get; init; }
    public bool CanGetBalance { get; init; }
    public bool CanPayBolt11 { get; init; }
    public bool CanPayAmountless
    {
        get => CanPayBolt11 && _canPayAmountless;
        init => _canPayAmountless = value;
    }
    public bool CanSetMaxFee { get; init; }
    public bool CanConnectPeer { get; init; }
    public bool CanOpenChannel { get; init; }
    public bool CanListChannels { get; init; }
    public bool HasAny =>
        CanGetInfo ||
        CanGetBalance ||
        CanPayBolt11 ||
        CanConnectPeer ||
        CanOpenChannel ||
        CanListChannels;
}
