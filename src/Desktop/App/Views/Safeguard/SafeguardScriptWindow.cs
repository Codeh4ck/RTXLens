using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using RTXLens.App.Controls;
using RTXLens.App.Views.Shell;
using RTXLens.Plugins.Msi.Contracts.Safeguard;
using RTXLens.Presentation.Msi.Afterburner;
using static RTXLens.App.Views.Configuration.ConfigurationPage;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Safeguard;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class SafeguardScriptWindow : LensWindow
{
    private readonly Dictionary<string, Control> _inputs = new(StringComparer.Ordinal);
    private readonly SafeguardBinding? _previous;
    private readonly ContentControl _message = Alert();
    private bool _busy;

    public SafeguardScriptWindow(SafeguardScriptDefinition script, SafeguardBinding? previous, string installation, int connector)
    {
        _previous = previous;
        Title = $"{script.Name} - connector {connector}";
        Width = script.SendsMail ? 720 : 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        CanMaximize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var previousMail = script.SendsMail && previous?.MailId is { } id
            ? new SafeguardStorage().Read<SafeguardMailCredential>("mail", id) : null;
        var fields = new StackPanel { Spacing = 10 };
        if (script.IsSimulation)
        {
            fields.Children.Add(Alert("Validation only. This action sends a real email. GPU settings and Windows power state are left alone.",
                "warning"));
        }

        var mailFields = new List<Control>();
        foreach (var parameter in script.Parameters.Values)
        {
            var input = CreateInput(parameter, previousMail);
            _inputs.Add(parameter.Name, input);
            var field = Field(parameter.Label, input);

            if (script.SendsMail && parameter.Name != "DelayMs")
            {
                mailFields.Add(field);
            }
            else
            {
                fields.Children.Add(field);
            }
        }

        foreach (var row in mailFields.Chunk(2))
        {
            fields.Children.Add(Columns(row));
        }

        var cancel = ActionButton("Cancel", "cancel-action", () => Close());
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var save = CommitButton("Save parameters", async void () => await RunAsync(SaveAsync));
        if (script.SendsMail)
        {
            var test = ActionButton("Send test email", "mail-action", async void () => await RunAsync(async () =>
            {
                var preview = new SafeguardMailPreview(script.Id, connector, ReadInteger("DelayMs"));
                SafeguardScripts.ValidateDelay(preview.DelayMs);
                SetAlert(_message, "Sending test email through the Windows service...");
                var result = await SubmitMailAsync(previousMail, preview);
                SetAlert(_message, result.Message, result.Success ? "success" : "error");
            }));
            footer.Children.Add(test);
        }

        footer.Children.Add(cancel);
        footer.Children.Add(save);
        Body = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = { fields, _message, footer } };
        Closing += (_, args) => args.Cancel = _busy;

        async Task SaveAsync()
        {
            var delay = ReadInteger("DelayMs");
            SafeguardScripts.ValidateDelay(delay);
            var candidate = await Task.Run(() => AfterburnerSafeguard.Create(installation, connector, script, delay,
                script.SendsMail ? Guid.NewGuid() : null));
            await AfterburnerSafeguard.ProbeAsync(script);
            if (script.SendsMail)
            {
                var result = await SubmitMailAsync(previousMail);
                if (!result.Success || result.MailId is null)
                {
                    throw new InvalidOperationException(result.Message);
                }

                candidate = candidate with { MailId = result.MailId };
            }

            _busy = false;
            Close(candidate);
        }

        async Task RunAsync(Func<Task> action)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            fields.IsEnabled = footer.IsEnabled = false;
            try
            {
                await action();
            }
            catch (Exception error) when (IsConfigurationError(error))
            {
                SetAlert(_message, error.Message, "error");
            }
            finally
            {
                _busy = false;
                fields.IsEnabled = footer.IsEnabled = true;
            }
        }
    }

    private Control CreateInput(SafeguardParameterDefinition field, SafeguardMailCredential? previousMail)
    {
        var mail = previousMail?.Settings;
        object? current = field.Name switch
        {
            "DelayMs" => _previous?.DelayMs,
            "Host" => mail?.Host,
            "Port" => mail?.Port,
            "Security" => mail?.Security,
            "Authentication" => mail is null ? null : mail.Authenticate ? "Username/password" : "Unauthenticated relay",
            "Username" => mail?.Username,
            "Sender" => mail?.Sender,
            "Recipients" => mail is null ? null : string.Join(", ", mail.Recipients),
            _ => null
        };
        current ??= field.Default;

        return field.Type switch
        {
            SafeguardParameterType.Integer => new ValidatedNumberInput(field.Label, (int)current!, field.Minimum!.Value, field.Maximum!.Value),
            SafeguardParameterType.Choice => new ComboBox { ItemsSource = field.Choices, SelectedItem = current },
            SafeguardParameterType.Secret => new TextBox
            {
                PasswordChar = '●',
                PlaceholderText = previousMail?.EncryptedPassword is not null ? "Leave blank to keep the saved password" : null
            },
            _ => new TextBox { Text = current as string }
        };
    }

    private int ReadInteger(string name) => checked((int)((ValidatedNumberInput)_inputs[name]).Read());

    private async Task<SafeguardMailResult> SubmitMailAsync(SafeguardMailCredential? previousMail, SafeguardMailPreview? preview = null)
    {
        var settings = new SafeguardMailSettings(Read("Host"), ReadInteger("Port"), Choice("Security"),
            Choice("Authentication") == "Username/password", Read("Username"), Read("Sender"),
            Read("Recipients").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        var password = ((TextBox)_inputs["Password"]).Text ?? string.Empty;
        var reusePassword = previousMail?.EncryptedPassword is not null
            && string.Equals(previousMail.Settings.Host, settings.Host, StringComparison.OrdinalIgnoreCase)
            && previousMail.Settings.Username == settings.Username;
        SafeguardScripts.ValidateMail(settings, password.Length > 0 || reusePassword);

        string? encrypted = null;
        if (settings.Authenticate && password.Length > 0)
        {
            var bytes = Encoding.UTF8.GetBytes(password);
            try
            {
                encrypted = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        var store = new SafeguardStorage();
        var id = Guid.NewGuid();
        store.Write("requests", id, new SafeguardMailRequest(DateTimeOffset.UtcNow, settings, encrypted,
            reusePassword ? _previous?.MailId : null, preview is not null) { Preview = preview });

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (!deadline.IsCancellationRequested)
            {
                if (store.Read<SafeguardMailResult>("results", id) is { } result)
                {
                    store.Delete("results", id);
                    return result;
                }

                await Task.Delay(250, deadline.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The service may be stopped or the SMTP server may be unreachable.
        }
        finally
        {
            store.Delete("requests", id);
        }

        return new(false, "No SMTP response from the Windows service. Check that it is running and try again.");

        string Read(string key) => ((TextBox)_inputs[key]).Text?.Trim() ?? string.Empty;

        string Choice(string key) => ((ComboBox)_inputs[key]).SelectedItem as string
                                     ?? throw new ArgumentException($"Select a value for {key}.");
    }

    internal static bool IsConfigurationError(Exception error) => error is IOException or UnauthorizedAccessException
        or ArgumentException or InvalidOperationException or CryptographicException or System.ComponentModel.Win32Exception
        or System.Text.Json.JsonException or OverflowException or System.Security.SecurityException;
}
