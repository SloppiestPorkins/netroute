using NetRoute.Core.Adapters;
using NetRoute.Core.Config;
using NetRoute.Core.Policy;
using NetRoute.Tests.Service;
using NetRoute.Windows.Wfp;
using Xunit;

namespace NetRoute.Tests.Windows;

/// <summary>
/// Review section G: Emergency Disable must remove what the engine holds under NetRoute's provider,
/// not only what this object remembers, and a rule that could not be installed must say it is not protected.
/// </summary>
public sealed class WfpEmergencyDisableTests
{
    private sealed class Store : IFilterStore
    {
        public Dictionary<ulong, string> Live { get; } = [];
        public List<ulong> Unknown { get; } = [];
        public bool FailEnumeration { get; set; }
        public bool FailAdd { get; set; }
        public int Transactions { get; private set; }
        public TimeSpan LastCommitDuration => TimeSpan.Zero;
        private ulong _next = 1000;

        public void InTransaction(Action action)
        {
            Transactions++;
            action();
        }

        public ulong Add(FilterSpec spec)
        {
            if (FailAdd && (spec.Name.Contains("block") || spec.Name.Contains("unavailable")))
            {
                throw new WfpException("FwpmFilterAdd0", WfpException.FWP_E_INVALID_PARAMETER);
            }
            var id = _next++;
            Live[id] = spec.Name;
            return id;
        }

        public List<ulong> Deleted { get; } = [];

        public void Delete(ulong id)
        {
            Live.Remove(id);
            Deleted.Add(id);
        }

        public IReadOnlyList<ulong> EnumerateOwned()
            => FailEnumeration ? throw new WfpException("FwpmFilterEnum0", 5) : [.. Live.Keys, .. Unknown];
    }

    private static EnforcementPlan Plan(RoleId role = RoleId.Gaming)
    {
        var gaming = NetRouteEngineTests.Ethernet();
        var downloads = NetRouteEngineTests.Wifi();
        var config = new NetRouteConfig
        {
            SetupCompleted = true,
            RouteSystemDownloads = false,
            RoleBindings =
            [
                new() { Role = RoleId.Gaming, AdapterLuid = gaming.Luid, AdapterGuid = gaming.Guid, LastKnownName = gaming.Name },
                new() { Role = RoleId.Downloads, AdapterLuid = downloads.Luid, AdapterGuid = downloads.Guid, LastKnownName = downloads.Name }
            ],
            AppRules = [AppRule.Create(AppIdentity.ForExecutable(@"C:\Games\Alpha\Alpha.exe", "Alpha"), role)]
        };
        return new PolicyResolver(new NetRouteEngineTests.MutableAdapterSource(gaming, downloads)).Resolve(config);
    }

    private static WfpEnforcer Enforcer(Store store)
        => new(store, 99, false, app => [app.ExecutablePath!], _ => true);

    [Fact]
    public void Filters_the_engine_holds_but_the_enforcer_lost_track_of_are_removed()
    {
        var store = new Store();
        store.Unknown.AddRange([5001, 5002]);
        Enforcer(store).Apply(Plan());

        var lost = Enforcer(store);       // a fresh enforcer: remembers nothing, but the engine still has filters
        lost.EmergencyDisable();

        Assert.Empty(store.Live);
        Assert.Contains(5001UL, store.Deleted);
        Assert.Contains(5002UL, store.Deleted);
        Assert.Equal(0, lost.InstalledFilterCount);
        Assert.Null(lost.LastEnumerationError);
    }

    [Fact]
    public void With_nothing_installed_and_nothing_listed_no_transaction_is_opened()
    {
        var store = new Store();
        Enforcer(store).EmergencyDisable();
        Assert.Equal(0, store.Transactions);
    }

    [Fact]
    public void A_listing_that_fails_does_not_stop_the_remembered_filters_being_removed()
    {
        var store = new Store();
        var enforcer = Enforcer(store);
        enforcer.Apply(Plan());
        Assert.NotEmpty(store.Live);

        store.FailEnumeration = true;
        enforcer.EmergencyDisable();

        Assert.Empty(store.Live);
        Assert.NotNull(enforcer.LastEnumerationError);
    }

    [Fact]
    public void A_listing_that_fails_with_nothing_remembered_does_not_throw()
    {
        var store = new Store { FailEnumeration = true };
        var enforcer = Enforcer(store);
        enforcer.EmergencyDisable();
        Assert.NotNull(enforcer.LastEnumerationError);
    }

    [Fact]
    public void A_rule_that_could_not_be_installed_says_it_is_not_protected()
    {
        var store = new Store { FailAdd = true };
        var result = Enforcer(store).Apply(Plan());

        var failure = Assert.Single(result.Failed);
        Assert.Contains("NOT protected", failure.Error.FriendlyMessage);
        Assert.DoesNotContain(store.Live.Values, n => n.StartsWith("Alpha:"));
    }

    [Fact]
    public void A_block_rule_that_could_not_be_installed_says_it_is_not_blocked()
    {
        var gaming = NetRouteEngineTests.Ethernet(AdapterState.Disconnected);
        var downloads = NetRouteEngineTests.Wifi();
        var config = new NetRouteConfig
        {
            SetupCompleted = true,
            RouteSystemDownloads = false,
            RoleBindings =
            [
                new() { Role = RoleId.Gaming, AdapterLuid = gaming.Luid, AdapterGuid = gaming.Guid, LastKnownName = gaming.Name },
                new() { Role = RoleId.Downloads, AdapterLuid = downloads.Luid, AdapterGuid = downloads.Guid, LastKnownName = downloads.Name }
            ],
            AppRules = [AppRule.Create(AppIdentity.ForExecutable(@"C:\Games\Alpha\Alpha.exe", "Alpha"), RoleId.Gaming)]
        };
        var plan = new PolicyResolver(new NetRouteEngineTests.MutableAdapterSource(gaming, downloads)).Resolve(config);
        Assert.Equal(EnforcementAction.BlockAll, plan.Applications[0].Action);

        var result = Enforcer(new Store { FailAdd = true }).Apply(plan);

        Assert.Contains("NOT being blocked", Assert.Single(result.Failed).Error.FriendlyMessage);
    }
}
