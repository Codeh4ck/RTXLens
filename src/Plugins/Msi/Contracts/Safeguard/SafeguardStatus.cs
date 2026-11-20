namespace RTXLens.Plugins.Msi.Contracts.Safeguard;

public static class SafeguardStatus
{
    public const string SubmittingShutdown = "Submitting shutdown";
    public const string ShutdownRequestUnconfirmed = "Shutdown request unconfirmed";
    public const string Interrupted = "Interrupted";
    public const string AcceptedBySmtpServer = "Accepted by SMTP server";
    public const string DeliveryExpired = "Delivery expired";
    public const string Simulated = "Simulated";
    public const string ShutdownRequested = "Shutdown requested";
    public const string ShutdownRequestFailed = "Shutdown request failed";
    public const string ScriptVerificationFailed = "Script verification failed";
    public const string SimulationEmailQueueFailed = "Simulation email queue failed";
}
