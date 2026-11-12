using System.Net;
using System.Net.Mail;

namespace RTXLens.Plugins.Msi.Contracts.Safeguard;

public enum SafeguardParameterType { Integer, Host, Choice, Text, Secret, Mailbox, Mailboxes }

public sealed record SafeguardParameterDefinition(string Name, string Label, SafeguardParameterType Type,
    object? Default = null, int? Minimum = null, int? Maximum = null, IReadOnlyList<string>? Choices = null);

public sealed record SafeguardScriptDefinition(string Id, string Name, string FileName,
    IReadOnlyDictionary<string, SafeguardParameterDefinition> Parameters)
{
    public static int Version => 1;
    public bool SendsMail => Parameters.ContainsKey("Host");
    public bool IsSimulation => Id == SafeguardScripts.Simulation;
    public override string ToString() => Name;
}

public static class SafeguardScripts
{
    public const string LowPower = "low-power-shutdown";
    public const string Notify = "low-power-shutdown-notify";
    public const string Simulation = "simulation";
    public const int DefaultRetentionDays = 14;
    public const int DefaultDelayMs = 5000;
    public const int MaximumDelayMs = 3_600_000;

    private static readonly SafeguardParameterDefinition Delay =
        new("DelayMs", "Shutdown delay (ms)", SafeguardParameterType.Integer, DefaultDelayMs, 0, MaximumDelayMs);

    private static readonly IReadOnlyDictionary<string, SafeguardParameterDefinition> NotificationParameters = Parameters(
        Delay,
        new("Host", "SMTP server", SafeguardParameterType.Host),
        new("Port", "SMTP port", SafeguardParameterType.Integer, 587, 1, 65535),
        new("Security", "Connection security", SafeguardParameterType.Choice, "STARTTLS", Choices: ["STARTTLS", "Implicit TLS"]),
        new("Authentication", "Authentication", SafeguardParameterType.Choice, "Username/password", Choices: ["Username/password", "Unauthenticated relay"]),
        new("Username", "Username", SafeguardParameterType.Text),
        new("Password", "Password or app password", SafeguardParameterType.Secret),
        new("Sender", "Sender", SafeguardParameterType.Mailbox),
        new("Recipients", "Recipients (comma separated)", SafeguardParameterType.Mailboxes, Minimum: 1, Maximum: 10));

    public static IReadOnlyList<SafeguardScriptDefinition> All { get; } = Array.AsReadOnly([
        new SafeguardScriptDefinition(LowPower, "Low power profile & shutdown", "LowPowerAndShutdown.ps1", Parameters(Delay)),
        new SafeguardScriptDefinition(Notify, "Low power profile, shutdown & notify", "LowPowerShutdownAndNotify.ps1", NotificationParameters),
        new SafeguardScriptDefinition(Simulation, "Simulation", "SimulateSafeguard.ps1", NotificationParameters)
    ]);

    public static SafeguardScriptDefinition Get(string id) => All.FirstOrDefault(script => script.Id == id)
        ?? throw new ArgumentException("Select a bundled Safeguard+ script.");

    public static void ValidateDelay(int delay) => CheckRange(Delay, delay);

    public static void ValidateMail(SafeguardMailSettings mail, bool hasPassword)
    {
        var definitions = Get(Notify).Parameters;
        CheckRange(definitions["Port"], mail.Port);

        if (string.IsNullOrWhiteSpace(mail.Host) || mail.Host.Length > 253
            || (Uri.CheckHostName(mail.Host) != UriHostNameType.Dns && !IPAddress.TryParse(mail.Host, out _)))
        {
            throw new ArgumentException("Enter a valid SMTP hostname or IP address.");
        }

        if (!definitions["Security"].Choices!.Contains(mail.Security))
        {
            throw new ArgumentException("Select STARTTLS or implicit TLS.");
        }

        if (mail.Authenticate && (string.IsNullOrWhiteSpace(mail.Username) || mail.Username.Length > 320
            || mail.Username.Any(char.IsControl) || !hasPassword))
        {
            throw new ArgumentException("Enter an SMTP username and password.");
        }

        CheckRange(definitions["Recipients"], mail.Recipients?.Length ?? 0);
        if (!IsMailbox(mail.Sender) || !mail.Recipients!.All(IsMailbox))
        {
            throw new ArgumentException("Enter valid sender and recipient email addresses.");
        }
    }

    private static bool IsMailbox(string address) => !string.IsNullOrWhiteSpace(address) && address.Length <= 320
        && !address.Any(char.IsControl) && MailAddress.TryCreate(address, out var parsed) && parsed.Address == address;

    private static void CheckRange(SafeguardParameterDefinition field, int value)
    {
        if (value < field.Minimum || value > field.Maximum)
        {
            throw new ArgumentException(field.Label + " must be between " + field.Minimum + " and " + field.Maximum + ".");
        }
    }

    private static IReadOnlyDictionary<string, SafeguardParameterDefinition> Parameters(params SafeguardParameterDefinition[] fields) =>
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, SafeguardParameterDefinition>(fields.ToDictionary(field => field.Name));
}