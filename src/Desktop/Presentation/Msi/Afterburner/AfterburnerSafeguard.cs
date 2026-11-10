using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using RTXLens.Contracts.Security;
using Microsoft.Win32;
using RTXLens.Plugins.Msi.Contracts.Safeguard;

namespace RTXLens.Presentation.Msi.Afterburner;

[SupportedOSPlatform("windows")]
public static class AfterburnerSafeguard
{
    internal const string HiddenWindowOption = "-WindowStyle Hidden ";
    private const string ArgumentPrefix = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File \"";
    private const string BindingMarker = "\" -BindingId ";
    private const string Workflow = "SafeguardWorkflow.ps1";

    public static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static string ScriptDirectory
    {
        get
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\RTXLens");
            var install = key?.GetValue("InstallDirectory") as string
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RTXLens");
            return Path.GetFullPath(Path.Combine(install, "Service", "SafeguardScripts"));
        }
    }

    public static string Arguments(SafeguardScriptDefinition script, Guid id) =>
        HiddenWindowOption + ArgumentPrefix + Path.Combine(ScriptDirectory, script.FileName) + BindingMarker + id.ToString("D");

    public static (Guid Id, string FileName)? Inspect(string application, string arguments)
    {
        if (!string.Equals(application, PowerShell, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var marker = arguments.LastIndexOf(BindingMarker, StringComparison.Ordinal);
        if (marker < 0 || !Guid.TryParseExact(arguments[(marker + BindingMarker.Length)..], "D", out var id))
        {
            return null;
        }

        var script = SafeguardScripts.All.FirstOrDefault(candidate =>
        {
            var hiddenArguments = Arguments(candidate, id);
            return string.Equals(arguments, hiddenArguments, StringComparison.OrdinalIgnoreCase)
                || string.Equals(arguments, hiddenArguments[HiddenWindowOption.Length..], StringComparison.OrdinalIgnoreCase);
        });
        return script is null ? null : (id, script.FileName);
    }

    public static SafeguardBinding Create(string installation, int connector, SafeguardScriptDefinition script, int delay, Guid? mailId)
    {
        var executable = Path.Combine(Path.GetFullPath(installation), "MSIAfterburner.exe");
        var entry = Path.Combine(ScriptDirectory, script.FileName);
        var workflow = Path.Combine(ScriptDirectory, Workflow);
        ProtectedInstallationFile.Require(executable);
        ProtectedInstallationFile.Require(entry);
        ProtectedInstallationFile.Require(workflow);
        var binding = new SafeguardBinding(Guid.NewGuid(), script.Id, SafeguardScriptDefinition.Version, connector, delay,
            executable, Hash(executable), Hash(entry), Hash(workflow), mailId);
        binding.Validate();
        return binding;
    }

    public static async Task ProbeAsync(SafeguardScriptDefinition script, CancellationToken token = default)
    {
        var path = Path.Combine(ScriptDirectory, script.FileName);
        ProtectedInstallationFile.Require(path);
        ProtectedInstallationFile.Require(Path.Combine(ScriptDirectory, Workflow));
        if (!File.Exists(PowerShell))
        {
            throw new InvalidOperationException("Windows PowerShell was not found.");
        }

        var start = new ProcessStartInfo(PowerShell)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-File", path, "-Probe" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows PowerShell could not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            throw new InvalidOperationException("PowerShell readiness check timed out.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("PowerShell could not run the Safeguard+ script. Check execution policy and administrator access.");
        }
    }

    public static async Task InstallBindingAsync(string installation, int connector, SafeguardBinding binding)
    {
        binding.Validate();
        var script = SafeguardScripts.Get(binding.ScriptId);
        var current = Create(installation, connector, script, binding.DelayMs, binding.MailId) with { Id = binding.Id };
        if (current != binding)
        {
            throw new InvalidOperationException("Afterburner or its bundled scripts changed. Configure the action again.");
        }

        var store = new SafeguardStorage();
        if (binding.MailId is { } id && store.Read<SafeguardMailCredential>("mail", id) is null)
        {
            throw new InvalidOperationException("Save SMTP settings through the service before applying this action.");
        }

        var existing = store.Read<SafeguardBinding>("bindings", binding.Id);
        if (existing is not null)
        {
            if (existing != binding)
            {
                throw new InvalidOperationException("The saved script binding changed. Configure the action again.");
            }

            return;
        }

        await ProbeAsync(script);
        _ = store.Location("history", Guid.Empty);
        _ = store.Location("outbox", Guid.Empty);
        store.Write("bindings", binding.Id, binding);
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
}