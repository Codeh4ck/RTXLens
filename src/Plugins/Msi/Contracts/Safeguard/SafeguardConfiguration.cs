namespace RTXLens.Plugins.Msi.Contracts.Safeguard;

public sealed record SafeguardMailSettings(string Host, int Port, string Security, bool Authenticate,
    string Username, string Sender, string[] Recipients);

public sealed record SafeguardMailCredential(SafeguardMailSettings Settings, string? EncryptedPassword);

public sealed record SafeguardBinding(Guid Id, string ScriptId, int Version, int Connector, int DelayMs,
    string AfterburnerPath, string ExecutableHash, string ScriptHash, string WorkflowHash, Guid? MailId)
{
    public void Validate()
    {
        var definition = SafeguardScripts.Get(ScriptId);
        SafeguardScripts.ValidateDelay(DelayMs);

        if (Id == Guid.Empty || Connector is not (1 or 2) || Version != SafeguardScriptDefinition.Version
            || !Path.IsPathFullyQualified(AfterburnerPath)
            || !string.Equals(Path.GetFileName(AfterburnerPath), "MSIAfterburner.exe", StringComparison.OrdinalIgnoreCase)
            || definition.SendsMail != (MailId is { } id && id != Guid.Empty)
            || !ValidHash(ExecutableHash) || !ValidHash(ScriptHash) || !ValidHash(WorkflowHash))
        {
            throw new ArgumentException("Invalid Safeguard+ script binding.");
        }
    }

    private static bool ValidHash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

public sealed record SafeguardMailRequest(DateTimeOffset CreatedAt, SafeguardMailSettings Settings,
    string? EncryptedInput, Guid? PreviousMailId, bool SendTest)
{
    public SafeguardMailPreview? Preview { get; init; }
}

public sealed record SafeguardMailPreview(string ScriptId, int Connector, int DelayMs);

public sealed record SafeguardMailResult(bool Success, string Message, Guid? MailId = null);