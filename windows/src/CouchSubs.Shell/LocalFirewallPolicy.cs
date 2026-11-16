// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Shell;

/// <summary>COM'dan bağımsız kural görünümü; izin kapsamı ve eski kuraldan geçiş test edilebilir.</summary>
public sealed record FirewallRule(string Name, string Program, bool Enabled, int Direction, int Action,
    int Profiles, int Protocol, string RemoteAddresses = "*", string LocalAddresses = "*",
    string LocalPorts = "*", string RemotePorts = "*", string ServiceName = "", string InterfaceTypes = "All",
    string Grouping = "", bool InterfaceRestricted = false, bool EdgeTraversal = false);

public interface IFirewallRuleStore
{
    IReadOnlyList<FirewallRule> Read();
    void Put(FirewallRule rule);
    void Remove(string name);
}

/// <summary>
/// TV ve Remote Play için yalnız Özel ağdaki yerel cihazlardan TCP/UDP izni. TV sunucusu dolu portta başka port
/// seçer; keşif ve PS5 yayını da değişken UDP portları kullanır. Bu nedenle port değil program/ağ kapsamı daraltılır.
/// </summary>
public static class LocalFirewallPolicy
{
    public const int PrivateProfile = 2;
    public const int Inbound = 1;
    public const int Allow = 1;
    public const int Block = 0;
    public const int Tcp = 6;
    public const int Udp = 17;
    public const int AnyProtocol = 256;
    public const string Group = "CouchSubs.LocalNetwork";
    public const string LegacyName = "CouchSubs";
    public const string TcpName = "CouchSubs - Local TCP";
    public const string UdpName = "CouchSubs - Local UDP";

    public static IReadOnlyList<FirewallRule> Required(string exe) =>
    [
        new(TcpName, exe, true, Inbound, Allow, PrivateProfile, Tcp, "LocalSubnet", Grouping: Group),
        new(UdpName, exe, true, Inbound, Allow, PrivateProfile, Udp, "LocalSubnet", Grouping: Group),
    ];

    public static bool SameProgram(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(a)),
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(b)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    private static bool Any(string value) => string.IsNullOrEmpty(value) || value == "*";
    private static bool LocalScope(string value) => Any(value) || value.Equals("LocalSubnet", StringComparison.OrdinalIgnoreCase);
    private static bool AllInterfaces(string value) => value.Equals("All", StringComparison.OrdinalIgnoreCase);
    private static bool SameName(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Yalnız uygulamanın önceki sürümünün oluşturduğu geniş kural otomatik taşınır.</summary>
    private static bool IsLegacy(FirewallRule r, string exe) => SameName(r.Name, LegacyName) && SameProgram(r.Program, exe)
        && r.Direction == Inbound && r.Action == Allow && r.Protocol == AnyProtocol
        && (r.Profiles & 7) == 7 && Any(r.RemoteAddresses) && Any(r.LocalAddresses)
        && Any(r.LocalPorts) && Any(r.RemotePorts) && r.ServiceName.Length == 0 && r.Grouping.Length == 0
        && AllInterfaces(r.InterfaceTypes) && !r.InterfaceRestricted && !r.EdgeTraversal;

    private static bool IsManaged(FirewallRule r, string exe) => (SameName(r.Name, TcpName) || SameName(r.Name, UdpName))
        && SameName(r.Grouping, Group) && SameProgram(r.Program, exe) && r.Direction == Inbound && r.Action == Allow;

    /// <summary>İzin Özel ağ içindir; Ortak/Etki alanı profillerinin açık olması aranmaz.</summary>
    public static bool HasRules(IReadOnlyList<FirewallRule> rules, string exe, bool privateFirewallEnabled = true) =>
        !rules.Any(r => IsLegacy(r, exe)) && (!privateFirewallEnabled || CoversLocalTraffic(rules, exe));

    private static bool CoversLocalTraffic(IReadOnlyList<FirewallRule> rules, string exe)
    {
        foreach (var protocol in new[] { Tcp, Udp })
        {
            var relevant = rules.Where(r => r.Enabled && r.Direction == Inbound && (r.Profiles & PrivateProfile) != 0
                && (r.Protocol == protocol || r.Protocol == AnyProtocol)).ToList();
            // Başka servislerin/uygulamaların engelleri bu programın izni sayılmaz. Genel tam engel ise izni ezer.
            if (relevant.Any(r => r.Action == Block && (SameProgram(r.Program, exe)
                || (r.Program.Length == 0 && r.ServiceName.Length == 0 && Any(r.LocalPorts)
                    && Any(r.RemotePorts) && LocalScope(r.RemoteAddresses) && Any(r.LocalAddresses))))) return false;
            if (!relevant.Any(r => r.Action == Allow && SameProgram(r.Program, exe) && LocalScope(r.RemoteAddresses)
                && Any(r.LocalAddresses) && Any(r.LocalPorts) && Any(r.RemotePorts)
                && r.ServiceName.Length == 0 && AllInterfaces(r.InterfaceTypes) && !r.InterfaceRestricted)) return false;
        }
        return true;
    }

    /// <summary>
    /// Önce dar kurallar eklenir ve doğrulanır, ancak sonra eski geniş kural kaldırılır. Kullanıcının/Windows'un
    /// engelleme kuralları silinmez. Aynı adlı yabancı kurallar varsa adla yapılan COM işlemi onlara dokunmamalı.
    /// </summary>
    public static bool Apply(IFirewallRuleStore store, string exe)
    {
        var before = store.Read();
        var legacy = before.Where(r => IsLegacy(r, exe)).ToList();
        if (before.Any(r => (SameName(r.Name, TcpName) || SameName(r.Name, UdpName)) && !IsManaged(r, exe))) return false;
        if (legacy.Count > 0 && before.Any(r => SameName(r.Name, LegacyName) && !IsLegacy(r, exe))) return false;
        // Engelleme kuralı varsa silerek geçmeyiz; önce kullanıcı/yönetici bu engeli değerlendirmeli.
        var intended = before.Where(r => !IsManaged(r, exe)).Concat(Required(exe)).ToList();
        if (!CoversLocalTraffic(intended, exe)) return false;
        foreach (var rule in Required(exe)) store.Put(rule);
        // Eski geniş izin yeni kuralların yazılamamasını maskelemesin.
        var installed = store.Read();
        if (!Required(exe).All(expected => installed.Any(r => r == expected))) return false;
        foreach (var rule in legacy) store.Remove(rule.Name);
        return HasRules(store.Read(), exe);
    }

    public static void RemoveOwned(IFirewallRuleStore store, string exe)
    {
        var rules = store.Read();
        foreach (var group in rules.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!group.All(r => IsManaged(r, exe) || IsLegacy(r, exe))) continue;
            foreach (var rule in group) store.Remove(rule.Name);
        }
    }
}
