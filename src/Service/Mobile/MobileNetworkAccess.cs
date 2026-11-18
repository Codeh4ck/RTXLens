using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using RTXLens.Contracts.Mobile;

namespace RTXLens.Service.Mobile;

internal sealed class MobileNetworkException(string message, Exception innerException) : IOException(message, innerException);

internal interface IMobileNetworkAccess
{
    void Apply(MobileNetworkRules rules);
    void Remove(MobileNetworkRules rules);
}

[SupportedOSPlatform("windows")]
internal sealed class MobileNetworkAccess : IMobileNetworkAccess
{
    public void Apply(MobileNetworkRules rules)
    {
        if (rules.OpenFirewall)
        {
            Run(() => AddFirewallRule(rules),
                "Windows Firewall could not add the Mobile Access rule. Run the service as administrator and retry.");
        }

        if (rules.PortForwarding is { } forwarding)
        {
            Run(() => AddMapping(rules, forwarding),
                "The router could not set up automatic forwarding. Check UPnP or configure forwarding manually.");
        }
    }

    public void Remove(MobileNetworkRules rules)
    {
        if (rules.OpenFirewall)
        {
            Run(() => RemoveFirewallRule(rules), "Windows Firewall could not remove the previous Mobile Access rule.");
        }

        if (rules.PortForwarding is { } forwarding)
        {
            Run(() => RemoveMapping(rules, forwarding),
                "The router could not remove the previous automatic forwarding rule. Check UPnP and retry.");
        }
    }

    private static void Run(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException
            or RuntimeBinderException or System.Security.SecurityException)
        {
            throw new MobileNetworkException(error is InvalidOperationException ? error.Message : message, error);
        }
    }

    private static void RemoveFirewallRule(MobileNetworkRules rules)
    {
        dynamic firewall = CreateComObject("HNetCfg.FwPolicy2");
        var name = RuleName(rules);
        if (FindFirewallRule(firewall.Rules, name) is not null)
        {
            firewall.Rules.Remove(name);
        }
    }

    private static void RemoveMapping(MobileNetworkRules rules, MobilePortForwarding forwarding)
    {
        var mappings = GetMappings();
        var mapping = FindMapping(mappings, forwarding.ExternalPort);
        if (mapping is not null && OwnsMapping(mapping, rules, forwarding))
        {
            mappings.Remove(forwarding.ExternalPort, "TCP");
        }
    }

    private static void AddFirewallRule(MobileNetworkRules rules)
    {
        var application = Environment.ProcessPath;
        if (!string.Equals(Path.GetFileName(application), "RTXLens.Service.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Automatic firewall setup requires the RTX Lens service executable.");
        }

        var name = RuleName(rules);
        dynamic firewall = CreateComObject("HNetCfg.FwPolicy2");

        if (FindFirewallRule(firewall.Rules, name) is not null)
        {
            return;
        }

        dynamic rule = CreateComObject("HNetCfg.FWRule");
        rule.Name = name;
        rule.Description = "Inbound RTX Lens mobile streaming and pairing.";
        rule.ApplicationName = application;
        rule.Protocol = 6; // TCP
        rule.LocalPorts = rules.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        rule.Direction = 1; // Inbound
        rule.Profiles = 3; // Domain and Private
        rule.Action = 1; // Allow
        rule.Enabled = true;
        firewall.Rules.Add(rule);
    }

    private static void AddMapping(MobileNetworkRules rules, MobilePortForwarding forwarding)
    {
        var address = IPAddress.Parse(forwarding.LocalAddress);
        var localAddress = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Any(unicast => unicast.Address.Equals(address));

        if (!localAddress)
        {
            throw new InvalidOperationException("The selected IPv4 address is no longer assigned to this PC.");
        }

        var mappings = GetMappings();
        var existing = FindMapping(mappings, forwarding.ExternalPort);

        if (existing is null)
        {
            mappings.Add(forwarding.ExternalPort, "TCP", rules.LocalPort, forwarding.LocalAddress, true, RuleName(rules));
        }
        else if (!OwnsMapping(existing, rules, forwarding))
        {
            throw new InvalidOperationException("The router already forwards the requested TCP port.");
        }
    }

    private static dynamic? FindFirewallRule(dynamic rules, string name)
    {
        foreach (dynamic rule in rules)
        {
            if (string.Equals(rule.Name, name, StringComparison.Ordinal))
            {
                return rule;
            }
        }

        return null;
    }

    private static dynamic? FindMapping(dynamic mappings, int port)
    {
        foreach (dynamic mapping in mappings)
        {
            if (mapping.ExternalPort == port && string.Equals(mapping.Protocol, "TCP", StringComparison.OrdinalIgnoreCase))
            {
                return mapping;
            }
        }

        return null;
    }

    private static bool OwnsMapping(dynamic mapping, MobileNetworkRules rules, MobilePortForwarding forwarding) =>
        string.Equals(mapping.Description, RuleName(rules), StringComparison.Ordinal)
        && mapping.InternalPort == rules.LocalPort
        && string.Equals(mapping.InternalClient, forwarding.LocalAddress, StringComparison.Ordinal);

    private static dynamic GetMappings()
    {
        dynamic router = CreateComObject("HNetCfg.NATUPnP");
        return router.StaticPortMappingCollection
            ?? throw new InvalidOperationException("The router does not provide automatic port forwarding.");
    }

    private static object CreateComObject(string programId) => Activator.CreateInstance(Type.GetTypeFromProgID(programId, true)!)!;

    private static string RuleName(MobileNetworkRules rules) => $"RTX Lens Mobile Access ({rules.Id:N})";
}
