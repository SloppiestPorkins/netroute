using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using NetRoute.Core.Adapters;
using NetRoute.Core.Config;
using NetRoute.Core.Policy;
using NetRoute.Windows.Split;
using NetRoute.Windows.Wfp;

namespace NetRoute.Poc;

/// <summary>
/// Measures what an apply costs the filter engine, at the size of the owner's real configuration,
/// and whether new connections from an unrelated program stall while it runs.
///
/// <para>It never filters a real program. It reads the real config only for its shape (how many
/// rules, how many program files each covers, which roles), then enforces that shape on zero-byte
/// stand-in .exe files in a temp folder, so the only effect is filter count and load.</para>
///
/// <para>While each apply runs, a second thread opens new TCP connections (to a local listener and
/// to 1.1.1.1:443) and records the slowest one, which is what an app opening connections would feel.</para>
/// </summary>
internal static class WfpMeasureProof
{
    public static int Run(bool? collapse)
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("This needs administrator rights to manage WFP filters. Re-run from an elevated terminal.");
                return 2;
            }
        }

        var root = Path.Combine(Path.GetTempPath(), "netroute-wfp-measure");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }

        try
        {
            return Measure(root, collapse);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static int Measure(string root, bool? collapse)
    {
        var real = new ConfigStore().Load();
        var discovery = new AdapterDiscovery();
        var resolver = new PolicyResolver(discovery);

        var rules = new List<AppRule>();
        var images = 0;
        var n = 0;
        foreach (var e in resolver.Resolve(real).Applications.Where(e => e.Action is EnforcementAction.PinToAdapter or EnforcementAction.BlockAll))
        {
            var app = e.Rule.App;
            AppIdentity stand;
            if (app.Kind == AppIdentityKind.Packaged)
            {
                stand = app;
                images++;
            }
            else
            {
                var count = Math.Max(1, SplitImagePaths.For(app).Count);
                images += count;
                var dir = Path.Combine(root, $"r{n++}");
                Directory.CreateDirectory(dir);
                for (var k = 0; k < count; k++)
                {
                    File.WriteAllBytes(Path.Combine(dir, k == 0 ? "main.exe" : $"p{k}.exe"), [0x4D, 0x5A]);
                }
                stand = app.Kind == AppIdentityKind.Folder
                    ? AppIdentity.ForFolder(dir, app.DisplayName)
                    : new AppIdentity { Kind = AppIdentityKind.Win32, DisplayName = app.DisplayName, ExecutablePath = Path.Combine(dir, "main.exe"), InstallLocation = dir };
            }
            rules.Add(e.Rule with { App = stand, Id = Guid.NewGuid() });
        }

        if (!rules.Any(r => r.App.Kind == AppIdentityKind.Folder))
        {
            var dir = Path.Combine(root, "library");
            Directory.CreateDirectory(dir);
            for (var k = 0; k < 128; k++)
            {
                File.WriteAllBytes(Path.Combine(dir, $"game{k}.exe"), [0x4D, 0x5A]);
            }
            rules.Add(AppRule.Create(AppIdentity.ForFolder(dir, "Games library"), RoleId.Gaming));
            images += 128;
            Console.WriteLine("Added a 128-program folder rule, as the config has none.");
        }
        Console.WriteLine($"{rules.Count} rules, {images} program files.");

        var plan = resolver.Resolve(real with { AppRules = rules });
        using var session = WfpSession.Open();
        var enforcer = new WfpEnforcer(session, discovery, collapse);
        Console.WriteLine($"Collapse program files: {collapse?.ToString() ?? $"default ({WfpEnforcer.CollapseProgramFilesByDefault})"}");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    (await listener.AcceptTcpClientAsync()).Dispose();
                }
                catch
                {
                    return;
                }
            }
        });

        var samples = new List<(double At, double Loopback, double External)>();
        var clock = Stopwatch.StartNew();
        var run = true;
        var probe = new Thread(() =>
        {
            while (run)
            {
                var lb = TimeConnect(IPAddress.Loopback, port);
                var ex = TimeConnect(IPAddress.Parse("1.1.1.1"), 443);
                lock (samples)
                {
                    samples.Add((clock.Elapsed.TotalMilliseconds, lb, ex));
                }
                Thread.Sleep(5);
            }
        })
        { IsBackground = true };
        probe.Start();

        (double, double) Slowest(double from, double to)
        {
            lock (samples)
            {
                var w = samples.Where(s => s.At >= from && s.At <= to).ToList();
                return w.Count == 0 ? (0, 0) : (w.Max(s => s.Loopback), w.Max(s => s.External));
            }
        }

        void Report(string label, Func<EnforcementResult> apply)
        {
            var from = clock.Elapsed.TotalMilliseconds;
            var r = apply();
            var to = clock.Elapsed.TotalMilliseconds;
            var (lb, ex) = Slowest(from - 20, to + 20);
            Console.WriteLine($"{label,-30} apply {r.Elapsed.TotalMilliseconds,7:F1} ms  commit {r.CommitTime.TotalMilliseconds,7:F1} ms  added {r.FiltersAdded,5}  removed {r.FiltersRemoved,5}  installed {r.FiltersInstalled,5}  failed {r.Failed.Count}  slowest new connection: loopback {lb:F1} ms, external {ex:F1} ms");
        }

        Thread.Sleep(1500);
        var (idleLb, idleEx) = Slowest(0, clock.Elapsed.TotalMilliseconds);
        Console.WriteLine($"idle, nothing applying: slowest new connection: loopback {idleLb:F1} ms, external {idleEx:F1} ms");

        Report("first apply", () => enforcer.Apply(plan));
        for (var k = 1; k <= 3; k++)
        {
            Thread.Sleep(300);
            Report($"nothing changed #{k}", () => enforcer.Apply(plan));
        }

        var flipped = rules.ToList();
        var victim = flipped.First(r => r.Role == RoleId.Gaming && r.App.Kind != AppIdentityKind.Packaged);
        flipped[flipped.IndexOf(victim)] = victim with { Role = RoleId.Downloads };
        Thread.Sleep(300);
        Report("one rule's role changed", () => enforcer.Apply(resolver.Resolve(real with { AppRules = flipped })));
        Thread.Sleep(300);
        Report("one rule removed", () => enforcer.Apply(resolver.Resolve(real with { AppRules = flipped.Skip(1).ToList() })));
        Thread.Sleep(300);
        Report("back to the full plan", () => enforcer.Apply(plan));

        run = false;
        probe.Join();
        listener.Stop();
        enforcer.EmergencyDisable();
        Console.WriteLine($"filters left after disable: {enforcer.InstalledFilterCount}");
        return 0;
    }

    private static double TimeConnect(IPAddress address, int port)
    {
        var t = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            client.ConnectAsync(address, port).Wait(6000);
        }
        catch
        {
        }
        return t.Elapsed.TotalMilliseconds;
    }
}
