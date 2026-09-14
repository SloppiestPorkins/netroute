using NetRoute.Core.Adapters;
using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;
using NetRoute.Ipc;

namespace NetRoute.App;

/// <summary>
/// An in-memory stand-in for the service, for `--demo`. It exists so the interface can be
/// looked at and checked (including by `--screenshot`) without touching the real network
/// configuration.
/// </summary>
public sealed class DemoNetRouteClient : INetRouteClient
{
    private readonly List<AdapterDto> _adapters =
    [
        Adapter(1, "Ethernet", "Realtek PCIe GbE Family Controller", AdapterKind.Ethernet, "1 Gbps", "192.168.0.51", true, true),
        Adapter(2, "Wi-Fi 2", "Realtek 8851BU Wireless LAN WiFi 6 USB NIC", AdapterKind.WiFi, "143 Mbps", "192.168.7.7", false, true),
        Adapter(3, "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", AdapterKind.Virtual, "10 Gbps", "172.27.160.1", false, false)
    ];

    private readonly List<AppRule> _rules = [];
    private readonly List<ServiceEventDto> _events = [];
    private bool _setup;
    private bool _paused;

    public DemoNetRouteClient(bool firstRun)
    {
        _setup = !firstRun;
        if (!firstRun)
        {
            _rules.Add(AppRule.Create(AppIdentity.ForExecutable(@"G:\SteamLibrary\steamapps\common\Halo Infinite\HaloInfinite.exe", "Halo Infinite"), RoleId.Gaming));
            _rules.Add(AppRule.Create(AppIdentity.ForExecutable(@"C:\Program Files (x86)\Steam\steam.exe", "Steam"), RoleId.Downloads));
            _rules.Add(AppRule.Create(AppIdentity.ForExecutable(@"C:\Users\Jack\AppData\Local\Discord\Discord.exe", "Discord"), RoleId.Gaming));
            _rules.Add(AppRule.Create(AppIdentity.ForExecutable(@"C:\Program Files\BraveSoftware\Brave.exe", "Brave"), RoleId.Downloads));
        }
    }

    public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<AdapterDto>> GetAdaptersAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AdapterDto>>(_adapters);

    public Task<IReadOnlyList<ConnectionDto>> GetConnectionsAsync(CancellationToken ct = default)
    {
        var list = new List<ConnectionDto>();
        void Add(string name, int pid, string? adapter, string ip, int tcp, int udp)
        {
            for (var i = 0; i < tcp; i++)
            {
                list.Add(new ConnectionDto { ProcessId = pid, ProcessName = name, Protocol = TransportProtocol.Tcp,
                    LocalEndpoint = $"{ip}:{50000 + i}", RemoteEndpoint = $"203.0.113.{i + 1}:443", InterfaceName = adapter, State = "Established" });
            }
            for (var i = 0; i < udp; i++)
            {
                list.Add(new ConnectionDto { ProcessId = pid, ProcessName = name, Protocol = TransportProtocol.Udp,
                    LocalEndpoint = $"{ip}:{60000 + i}", InterfaceName = adapter });
            }
        }
        Add("steam", 4120, "Wi-Fi 2", "192.168.7.7", 14, 0);
        Add("steamwebhelper", 4188, "Wi-Fi 2", "192.168.7.7", 6, 0);
        Add("HaloInfinite", 7788, "Ethernet", "192.168.0.51", 3, 2);
        Add("Discord", 5021, "Ethernet", "192.168.0.51", 4, 1);
        Add("brave", 9001, "Wi-Fi 2", "192.168.7.7", 9, 1);
        Add("svchost", 1200, "Wi-Fi 2", "192.168.7.7", 2, 0);
        Add("Spotify", 6400, null, "0.0.0.0", 0, 3);
        return Task.FromResult<IReadOnlyList<ConnectionDto>>(list);
    }

    public Task<ServiceStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        RoleStatusDto Role(RoleId role, AdapterDto a, int latency, double loss) => new()
        {
            Role = role, Adapter = a, Health = RoleHealth.Connected, LatencyMs = latency, PacketLossPercent = loss,
            InternetReachable = true, AssignedApps = _rules.Count(r => r.Role == role), Ipv4Only = !a.HasIpv6Route
        };

        var apps = _rules.Select(r =>
        {
            var adapter = r.Role == RoleId.Gaming ? _adapters[0] : _adapters[1];
            var verified = r.App.DisplayName != "Brave";
            return new AppStatusDto
            {
                Rule = r,
                Action = r.Role == RoleId.Default || _paused ? EnforcementAction.None : EnforcementAction.PinToAdapter,
                Verification = r.Role == RoleId.Default || _paused ? VerificationState.NotEnforced
                    : verified ? VerificationState.Verified : VerificationState.Configured,
                ResolvedAdapterName = adapter.Name,
                ObservedAdapterName = verified ? adapter.Name : null,
                ActiveConnections = verified ? 6 : 0,
                PreexistingConnections = 0,
                Reasons =
                [
                    Reason.Ok($"You assigned {r.App.DisplayName} to {r.Role.DisplayName()}."),
                    Reason.Ok($"{r.Role.DisplayName()} currently points to {adapter.Name}."),
                    Reason.Ok($"{adapter.Name} is connected."),
                    Reason.Ok("Strict mode is enabled, so no other network may be used.")
                ],
                VerificationSummary = verified ? $"Actual traffic verified on {adapter.Name}." : $"Applied for {adapter.Name}; waiting to see traffic."
            };
        }).ToList();

        return Task.FromResult(new ServiceStatusDto
        {
            ProtocolVersion = IpcProtocol.Version,
            SetupCompleted = _setup,
            EnforcementActive = !_paused,
            EnforcementPaused = _paused,
            RedirectionAvailable = true,
            RedirectSummary = $"On. {_rules.Count(r => r.Role == RoleId.Gaming)} Gaming apps are moved onto Ethernet; Wi-Fi 2 is Windows' default connection.",
            Roles = [Role(RoleId.Gaming, _adapters[0], 12, 0), Role(RoleId.Downloads, _adapters[1], 21, 0.4)],
            Apps = apps,
            RecentLeaks = [],
            RecentEvents = _events,
            GeneratedAt = DateTimeOffset.Now
        });
    }

    public Task CompleteSetupAsync(ulong gamingLuid, ulong downloadsLuid, CancellationToken ct = default)
    {
        _setup = true;
        return Task.CompletedTask;
    }

    public Task<RoleChangeResultDto> SetRoleAdapterAsync(RoleId role, ulong adapterLuid, CancellationToken ct = default)
    {
        var name = _adapters.First(a => a.Luid == adapterLuid).Name;
        var count = _rules.Count(r => r.Role == role);
        return Task.FromResult(new RoleChangeResultDto(role, name, count, $"{role.DisplayName()} is now {name}. {count} applications updated."));
    }

    public Task<AppStatusDto> AddRuleAsync(AppIdentity app, RoleId role, CancellationToken ct = default)
    {
        _rules.Add(AppRule.Create(app, role));
        return GetStatusAsync(ct).ContinueWith(t => t.Result.Apps.Last(), ct);
    }

    public Task UpdateRuleAsync(AppRule rule, CancellationToken ct = default)
    {
        var i = _rules.FindIndex(r => r.Id == rule.Id);
        if (i >= 0)
        {
            _rules[i] = rule;
        }
        return Task.CompletedTask;
    }

    public Task RemoveRuleAsync(Guid ruleId, CancellationToken ct = default)
    {
        _rules.RemoveAll(r => r.Id == ruleId);
        return Task.CompletedTask;
    }

    public Task SetRulePausedAsync(Guid ruleId, bool paused, CancellationToken ct = default)
    {
        var i = _rules.FindIndex(r => r.Id == ruleId);
        if (i >= 0)
        {
            _rules[i] = _rules[i] with { Paused = paused };
        }
        return Task.CompletedTask;
    }

    public Task SetEnforcementPausedAsync(bool paused, CancellationToken ct = default)
    {
        _paused = paused;
        return Task.CompletedTask;
    }

    public Task EmergencyDisableAsync(CancellationToken ct = default)
    {
        _paused = true;
        return Task.CompletedTask;
    }

    private static AdapterDto Adapter(ulong luid, string name, string description, AdapterKind kind, string speed, string ip, bool v6, bool selectable) => new()
    {
        Luid = luid, Guid = $"{{{luid}}}", Name = name, Description = description, Kind = kind,
        State = AdapterState.Connected, LinkSpeed = speed, Ipv4Address = ip, HasIpv6Route = v6,
        Gateways = [], DnsServers = [], InterfaceIndex = (uint)luid, Selectable = selectable
    };
}
