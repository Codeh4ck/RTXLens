namespace RTXLens.Plugins.WireView.Contracts.Warnings;

public static class WireViewWarningIds
{
    public const string Overtemperature = WireViewIdentity.Id + "/overtemperature";
    public const string ProfilePinOvercurrent = WireViewIdentity.Id + "/profile-pin-overcurrent";
    public const string Overcurrent = WireViewIdentity.Id + "/total-overcurrent";
    public const string PinOvercurrent = WireViewIdentity.Id + "/pin-overcurrent";
    public const string Overpower = WireViewIdentity.Id + "/overpower";
    public const string Imbalance = WireViewIdentity.Id + "/current-imbalance";
    public const string UnknownFault = WireViewIdentity.Id + "/unknown-fault";
}
