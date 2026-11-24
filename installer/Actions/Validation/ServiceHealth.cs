using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RTXLens.Installer.Actions.Validation;

public static class ServiceHealth
{
    public static bool IsHealthy(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var health = (HealthResponse?)new DataContractJsonSerializer(typeof(HealthResponse)).ReadObject(stream);
        return health is { Status: "running", ConfigurationRevision: > 0 };
    }

    public static void Wait(string expectedVersion)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            try
            {
                if (IsHealthy(Read("health")) && HasVersion(Read("version"), expectedVersion))
                {
                    return;
                }
            }
            catch (Exception error) when (error is WebException or IOException or System.Runtime.Serialization.SerializationException or ArgumentException or InvalidOperationException)
            {
                // The service may still be initializing, no hardware needs to be reporting
            }

            Thread.Sleep(500);
        } while (DateTime.UtcNow < deadline);

        throw new IOException("RTX Lens did not pass its health and version checks within 30 seconds. See the Application event log and installer log. Installation will roll back.");
    }

    public static bool HasVersion(string json, string expectedVersion)
    {
        var manifest = new JavaScriptSerializer().DeserializeObject(json);

        return manifest is System.Collections.Generic.Dictionary<string, object> fields
            && fields.TryGetValue("version", out var version) && version is string value
            && string.Equals(value, expectedVersion, StringComparison.Ordinal);
    }

    private static string Read(string endpoint)
    {
        var request = WebRequest.CreateHttp("http://localhost:59273/" + endpoint);
        request.Timeout = request.ReadWriteTimeout = 2000;
        request.Proxy = null!;

        using var response = request.GetResponse();
        using var reader = new StreamReader(response.GetResponseStream()!);

        return reader.ReadToEnd();
    }
}