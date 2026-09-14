using NetRoute.Core.Policy;

namespace NetRoute.Windows.Split;

/// <summary>The result of applying a <see cref="RedirectPlan"/>, for status and events.</summary>
public sealed record RedirectOutcome(bool Engaged, int SplitImages, string? RouteNote, string? Problem);

/// <summary>
/// Owns the split-tunnel driver and the default-route change on the service's behalf.
///
/// <para>Holds the driver's single handle for as long as the service runs, and applies a plan
/// only when its fingerprint changes. The service reconciles every 10 seconds, and
/// re-registering an unchanged configuration each time would be pointless churn in a kernel
/// driver.</para>
///
/// <para>Turning off always means Reset, never stopping the driver: stopping it un-reset
/// crashes Windows. Dispose resets the driver and restores the original default route, so
/// stopping the service leaves the machine as it found it.</para>
/// </summary>
public sealed class SplitTunnelController : IDisposable
{
    private readonly DefaultRouteManager _routes;
    private SplitTunnelDriver? _driver;
    private bool _initialized;
    private string? _fingerprint;
    private RedirectOutcome _last = new(false, 0, null, "Not applied yet.");

    public SplitTunnelController(DefaultRouteManager routes)
    {
        _routes = routes;
        TryOpen();
    }

    /// <summary>True when the driver's device is open and NetRoute can move apps.</summary>
    public bool Available => TryOpen();

    public string? UnavailableReason { get; private set; }

    public RedirectOutcome Apply(RedirectPlan plan)
    {
        if (plan.Fingerprint == _fingerprint)
        {
            return _last;
        }

        string? routeNote = null;
        try
        {
            if (plan.ManageDefaultRoute)
            {
                routeNote = _routes.EnsurePreferred(plan.Downloads!, plan.Gaming!);
            }
            else
            {
                _routes.Restore();
            }
        }
        catch (Exception ex)
        {
            routeNote = $"NetRoute couldn't change Windows' default connection: {ex.Message}";
        }

        RedirectOutcome outcome;
        if (!plan.Split)
        {
            Disengage();
            outcome = new(false, 0, routeNote, plan.Reason);
        }
        else if (!TryOpen())
        {
            outcome = new(false, 0, routeNote, UnavailableReason);
        }
        else
        {
            var images = plan.SplitRules
                .SelectMany(r => SplitImagePaths.For(r.App))
                .Select(ToNtPath)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (images.Count == 0)
            {
                Disengage();
                outcome = new(false, 0, routeNote, "NetRoute couldn't find the program files of your Gaming apps.");
            }
            else
            {
                try
                {
                    if (!_initialized)
                    {
                        SplitTunnelSublayers.EnsureCreated();
                        _driver!.Reinitialize(SplitTunnelSublayers.Baseline, SplitTunnelSublayers.Dns);
                        _initialized = true;
                    }

                    // Tunnel = kept off (Downloads, the default route); internet = moved onto (Gaming).
                    _driver!.RegisterIpAddresses(
                        plan.Downloads!.Ipv4Address, plan.Gaming!.Ipv4Address,
                        plan.Downloads.Ipv6Address, plan.Gaming.Ipv6Address);
                    _driver.SetConfiguration(images);

                    var engaged = _driver.GetState() == SplitTunnelState.Engaged;
                    outcome = new(engaged, images.Count, routeNote, engaged ? null : "The split-tunnel driver didn't engage.");
                }
                catch (Exception ex)
                {
                    Disengage();
                    outcome = new(false, 0, routeNote, ex.Message);
                }
            }
        }

        // A failed apply is retried on the next reconcile rather than cached.
        _fingerprint = outcome.Problem is null || !plan.Split ? plan.Fingerprint : null;
        _last = outcome;
        return outcome;
    }

    /// <summary>Forget the last applied plan, so the next reconcile applies it again.</summary>
    public void Invalidate() => _fingerprint = null;

    /// <summary>Stop moving apps. Resets the driver (never unloads it).</summary>
    public void Disengage()
    {
        try
        {
            _driver?.Reset();
        }
        catch (Exception)
        {
            // Best effort. A handle that has gone bad is reopened on the next apply.
            _driver?.Dispose();
            _driver = null;
        }
        _initialized = false;
    }

    /// <summary>Stop moving apps and put Windows' default connection back as it was (§43).</summary>
    public void DisableAll()
    {
        Disengage();
        _routes.Restore();
        _fingerprint = null;
        _last = new(false, 0, null, "Turned off.");
    }

    public void Dispose()
    {
        try
        {
            DisableAll();
        }
        finally
        {
            _driver?.Dispose();
            _driver = null;
        }
    }

    private bool TryOpen()
    {
        if (_driver is not null)
        {
            return true;
        }
        try
        {
            _driver = SplitTunnelDriver.Open();
            UnavailableReason = null;
            return true;
        }
        catch (SplitTunnelException ex)
        {
            UnavailableReason = ex.Message;
            return false;
        }
    }

    private static string? ToNtPath(string path)
    {
        try
        {
            return DevicePaths.ToNtPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
