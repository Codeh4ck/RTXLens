namespace RTXLens.Contracts.Monitoring;

public static class MobileProtocol
{
    public const string Session = "/session";
    public const string Incidents = "/incidents";
    public const string Pairing = "/pairing";
    public const string CompletePairing = "/pairing/complete";
    public const string Registration = "/notification-registration";
    public const string RegistrationV2 = "/notification-registration/v2";
    public const string WarningActions = "/warning-actions";
    public const string RunWarningAction = "/warning-actions/run";
    public const string WarningChannel = "rtxlens-warnings";
}
