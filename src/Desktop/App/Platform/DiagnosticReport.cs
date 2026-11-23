using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO.Compression;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RTXLens.Contracts.Diagnostics;

namespace RTXLens.App.Platform;

internal sealed partial class DiagnosticReport(string folder, DateTimeOffset from, DateTimeOffset through, CancellationToken token)
{
    private readonly List<string> _notes = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static Task<string> CollectAsync(DirectoryInfo workspace, Uri service, DateTimeOffset from,
        DateTimeOffset through, IProgress<string> progress, CancellationToken token) => Task.Run(async () =>
    {
        if (from >= through)
        {
            throw new ArgumentException("Choose a start time earlier than the end time.");
        }

        if (!service.IsLoopback || service.Scheme is not ("http" or "https") || service.PathAndQuery != "/" || service.UserInfo.Length != 0 || service.Fragment.Length != 0)
        {
            throw new ArgumentException("Diagnostic collection requires a local RTX Lens service address.");
        }

        var folder = workspace.CreateSubdirectory("report").FullName;
        var report = new DiagnosticReport(folder, from, through, token);
        progress.Report("Collecting desktop reports and Windows events…");

        report.CollectDesktop(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        report.TryCollect("Windows Application events", report.CollectWindowsEvents);
        report.CollectSystem();

        if (OperatingSystem.IsWindows())
        {
            progress.Report("Identifying processors, memory, graphics adapters and power supplies…");
            report.TryCollect("Hardware inventory", report.CollectHardwareInventory);
        }

        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        http.BaseAddress = service;
        http.Timeout = TimeSpan.FromSeconds(5);
        http.MaxResponseContentBufferSize = 8 * 1024 * 1024;

        progress.Report("Collecting service history…");

        await report.CollectHistoryAsync(http);

        progress.Report("Capturing current devices and live readings…");
        await report.CollectLiveAsync(http);

        await File.WriteAllLinesAsync(report.PathFor("summary.txt"), new[]
        {
            "RTX Lens diagnostic report", $"From (UTC): {from.UtcDateTime:O}", $"Through (UTC): {through.UtcDateTime:O}",
            $"Collected (UTC): {DateTime.UtcNow:O}",
            "desktop/ and history/ contain records in the selected interval. current/ and live/ are snapshots taken during collection.", ""
        }.Concat(report._notes), token);

        progress.Report("Preparing the ZIP file…");

        var output = Path.Combine(workspace.FullName, "RTXLens-Diagnostics.zip");
        await ZipFile.CreateFromDirectoryAsync(folder, output, CompressionLevel.Fastest, false, token);

        return output;
    }, token);

    private string PathFor(string entry)
    {
        var path = Path.Combine(folder, entry);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private void TryCollect(string source, Action collect)
    {
        token.ThrowIfCancellationRequested();

        try
        {
            collect();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _notes.Add($"{source}: {error.GetType().Name}");
        }
    }

    private bool InRange(DateTimeOffset time) => time >= from && time <= through;

    [GeneratedRegex(@"^(?:crash|action)-(?<time>\d{8}T\d{13}Z)-\d+-[0-9a-f]{32}\.txt$", RegexOptions.CultureInvariant)]
    private static partial Regex ReportName();

    private void CollectDesktop(string localData)
    {
        foreach (var product in new[] { "RTXLens", "RTXLens.Debug" })
        {
            var root = Path.Combine(localData, product);

            TryCollect(product + " desktop reports", () =>
            {
                var reports = Path.Combine(root, "Diagnostics");
                var count = 0;

                if (Directory.Exists(reports))
                {
                    foreach (var file in Directory.EnumerateFiles(reports, "*.txt"))
                    {
                        token.ThrowIfCancellationRequested();

                        var name = Path.GetFileName(file);

                        if (DateTimeOffset.TryParseExact(ReportName().Match(name).Groups["time"].Value,
                                "yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) && InRange(timestamp))
                        {
                            TryCollect(product + " crash/action report", () =>
                            {
                                var report = SupportReportData.Crash(file, timestamp, name.StartsWith("crash-", StringComparison.Ordinal));

                                File.WriteAllText(PathFor($"desktop/{product}/report-{timestamp.UtcDateTime:yyyyMMddTHHmmssfffffffZ}-{count + 1}.json"),
                                    JsonSerializer.Serialize(report, Json));

                                count++;
                            });
                        }
                    }
                }

                _notes.Add($"{product} desktop crash/action reports: {count}");
            });

            TryCollect(product + " desktop events", () =>
            {
                var path = Path.Combine(root, "Avalonia", "desktop-events.json");
                if (!File.Exists(path))
                {
                    return;
                }

                using var input = File.OpenRead(path);
                var entries = JsonSerializer.Deserialize<ServiceLogEntry[]>(input, Json) ?? [];

                File.WriteAllText(PathFor($"desktop/{product}/desktop-events.json"),
                    JsonSerializer.Serialize(entries.Where(entry => InRange(entry.Timestamp)).Select(SupportReportData.Event), Json));
            });
        }
    }

    private void CollectWindowsEvents()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Event Log is required.");
        }

        var start = from.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        var end = through.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        var filter = $"*[System[TimeCreated[@SystemTime >= '{start}' and @SystemTime <= '{end}'] and "
            + "(Provider[@Name='.NET Runtime'] or Provider[@Name='Application Error'] or "
            + "Provider[@Name='Windows Error Reporting'] or Provider[@Name='Application Hang'])]]";

        using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, filter));
        using var writer = File.CreateText(PathFor("history/windows-application-events.jsonl"));

        var count = 0;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            using var record = reader.ReadEvent();
            if (record is null)
            {
                break;
            }

            var xml = record.ToXml();
            string? message = null;

            try
            {
                message = record.FormatDescription();
            }
            catch (EventLogException)
            {
                /* Raw event fields remain available. */
            }

            if (!IsLensEvent(xml) && !IsLensEvent(message))
            {
                continue;
            }

            writer.WriteLine(JsonSerializer.Serialize(new
            {
                timestamp = record.TimeCreated?.ToUniversalTime(), provider = record.ProviderName, eventId = record.Id,
                process = (xml.Contains("RTXLens.Service.exe", StringComparison.OrdinalIgnoreCase)
                    || message?.Contains("RTXLens.Service.exe", StringComparison.OrdinalIgnoreCase) == true) ? "RTX Lens Service" : "RTX Lens Desktop"
            }, Json));

            count++;
        }

        _notes.Add($"Matching Windows crash/hang events: {count}");
    }

    private static bool IsLensEvent(string? text) => text?.Contains("RTXLens.App.exe", StringComparison.OrdinalIgnoreCase) == true
                                                     || text?.Contains("RTXLens.Service.exe", StringComparison.OrdinalIgnoreCase) == true;

    private void CollectSystem()
    {
        var system = new Dictionary<string, string?>
        {
            ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["windowsVersion"] = Environment.OSVersion.VersionString,
            ["runningDesktopVersion"] = SupportReportData.VersionNumber(FileVersionInfo.GetVersionInfo(typeof(DiagnosticReport).Assembly.Location).ProductVersion)
        };

        if (OperatingSystem.IsWindows())
        {
            CollectWindowsSystem(system);
        }

        File.WriteAllText(PathFor("current/system.json"), JsonSerializer.Serialize(system, Json));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void CollectWindowsSystem(Dictionary<string, string?> system)
    {
        TryCollect("Installed Windows and desktop versions", () =>
        {
            const string windows = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

            if (int.TryParse(Registry.GetValue(windows, "CurrentBuildNumber", null) as string, out var build) && Registry.GetValue(windows, "UBR", null) is int revision)
            {
                system["windowsBuild"] = $"{build}.{revision}";
            }

            if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\RTXLens", "InstallDirectory", null) is string install)
            {
                system["installedDesktopVersion"] = SupportReportData.VersionNumber(FileVersionInfo.GetVersionInfo(Path.Combine(install, "Desktop", "RTXLens.App.exe")).ProductVersion);
            }
        });

        TryCollect("Windows service status", () =>
        {
            using var service = new ServiceController("RTXLensService");
            system["serviceStatus"] = service.Status.ToString();
        });
    }

    internal static async Task<bool> TryCleanUpAsync(Action delete)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                delete();
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3)
                {
                    return false;
                }

                await Task.Delay(100 * attempt).ConfigureAwait(false);
            }
        }
    }

    public static async Task SaveAsync(string archive, string destination, CancellationToken token)
    {
        var pending = Path.Combine(Path.GetDirectoryName(destination)!, $".rtxlens-{Guid.NewGuid():N}.tmp");
        var ownsPending = false;

        try
        {
            await using (var input = File.OpenRead(archive))
            await using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsPending = true;
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();

            File.Move(pending, destination, overwrite: true);
            ownsPending = false;
        }
        finally
        {
            if (ownsPending && !await TryCleanUpAsync(() => File.Delete(pending)).ConfigureAwait(false))
            {
                throw new IOException($"Could not remove the incomplete temporary ZIP at {pending}.");
            }
        }
    }
}
