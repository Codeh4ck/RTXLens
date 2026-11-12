using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace RTXLens.UI.Tests.BuildVerification;

internal static class ReleaseBuildVerification
{
    public static void Run(string directory)
    {
        using var stream = File.OpenRead(Path.Combine(directory, "RTXLens.App.dll"));
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var types = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition).ToArray();
        if (!types.Any(type => metadata.GetString(type.Namespace) == "RTXLens.App.Views.Simulation"
                && metadata.GetString(type.Name) == "DebugConsoleWindow")
            || !File.Exists(Path.Combine(directory, "RTXLens.Debugging.dll")))
        {
            throw new InvalidOperationException("Release is missing the telemetry debug console or its simulation library.");
        }

        foreach (var type in types)
        {
            var typeNamespace = metadata.GetString(type.Namespace);
            var typeName = metadata.GetString(type.Name);
            if (typeNamespace.StartsWith("RTXLens.App.Debugging", StringComparison.Ordinal)
                || typeNamespace.StartsWith("RTXLens.App.Demo", StringComparison.Ordinal)
                || typeNamespace.StartsWith("RTXLens.App.Diagnostics", StringComparison.Ordinal)
                || typeName == "AfterburnerChangesWindow")
            {
                throw new InvalidOperationException($"Release includes a developer-only type: {typeNamespace}.{typeName}");
            }
        }

        Console.WriteLine("PASS: Release includes the telemetry debug console and excludes developer-only views.");
    }
}
