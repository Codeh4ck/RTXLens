using System.Reflection;

namespace RTXLens.App;

internal static class DesktopErrorReports
{
    internal const int RetainedPerKind = 8;

    internal static string? Write(Exception error, string operation, bool crash = false, string? directory = null)
    {
        try
        {
            directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Platform.DesktopBuild.DataDirectoryName, "Diagnostics");
            Directory.CreateDirectory(directory);
            var now = DateTimeOffset.UtcNow;
            var kind = crash ? "crash" : "action";
            var path = Path.Combine(directory, $"{kind}-{now:yyyyMMddTHHmmssfffffffZ}-{Environment.ProcessId}-{Guid.NewGuid():N}.txt");
            var assembly = typeof(Program).Assembly;
            var build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString();
            File.WriteAllText(path, $"{now:O} · RTX Lens {build}\n{operation}\n{error}");
            foreach (var older in Directory.GetFiles(directory, $"{kind}-*.txt").OrderDescending(StringComparer.Ordinal).Skip(RetainedPerKind))
            {
                File.Delete(older);
            }
            return path;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            System.Diagnostics.Trace.WriteLine(failure);
            return null;
        }
    }
}
