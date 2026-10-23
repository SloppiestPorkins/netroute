using NetRoute.Core.Adapters;
using NetRoute.Core.Config;
using NetRoute.Core.Policy;
using NetRoute.Tests.Service;
using NetRoute.Windows.Wfp;

namespace NetRoute.Tests.Windows;

/// <summary>
/// The enforcer only changes what differs. These run against a fake filter engine, so they say
/// nothing about WFP itself; they pin down which filters an apply adds and deletes.
/// </summary>
public sealed class WfpEnforcerDiffTests
{
    private const ulong LoopbackLuid = 99;

    private sealed class FakeStore : IFilterStore
    {
        public Dictionary<ulong, FilterSpec> Live { get; private set; } = [];
        public int Transactions { get; private set; }
        public List<FilterSpec> Added { get; } = [];
        public List<ulong> Deleted { get; } = [];
        public Func<FilterSpec, bool>? FailAdd { get; set; }
        public bool FailCommit { get; set; }
        public TimeSpan LastCommitDuration { get; } = TimeSpan.FromMilliseconds(7);
        private ulong _next = 1;
        private bool _inTransaction;

        public void InTransaction(Action action)
        {
            Transactions++;
            var snapshot = new Dictionary<ulong, FilterSpec>(Live);
            _inTransaction = true;
            try
            {
                action();
                if (FailCommit)
                {
                    throw new WfpException("FwpmTransactionCommit0", 1);
                }
            }
            catch
            {
                Live = snapshot;
                throw;
            }
            finally
            {
                _inTransaction = false;
            }
        }

        public ulong Add(FilterSpec spec)
        {
            Assert.True(_inTransaction, "filters must only be added inside a transaction");
            if (FailAdd?.Invoke(spec) == true)
            {
                throw new WfpException($"FwpmFilterAdd0({spec.Name})", WfpException.FWP_E_INVALID_PARAMETER);
            }
            var id = _next++;
            Live[id] = spec;
            Added.Add(spec);
            return id;
        }

        public void Delete(ulong id)
        {
            Assert.True(_inTransaction, "filters must only be deleted inside a transaction");
            Live.Remove(id);
            Deleted.Add(id);
        }

        public void ResetCounters()
        {
            Added.Clear();
            Deleted.Clear();
        }
    }

    private static readonly NetworkAdapter Gaming = NetRouteEngineTests.Ethernet();
    private static readonly NetworkAdapter Downloads = NetRouteEngineTests.Wifi();

    private static AppRule Rule(string name, RoleId role = RoleId.Gaming, string? exe = null)
        => AppRule.Create(AppIdentity.ForExecutable(exe ?? $@"C:\Games\{name}\{name}.exe", name), role);

    private static EnforcementPlan PlanFor(params AppRule[] rules)
    {
        var source = new NetRouteEngineTests.MutableAdapterSource(Gaming, Downloads);
        var config = new NetRouteConfig
        {
            SetupCompleted = true,
            RouteSystemDownloads = false,
            RoleBindings = [Bind(RoleId.Gaming, Gaming), Bind(RoleId.Downloads, Downloads)],
            AppRules = [.. rules]
        };
        return new PolicyResolver(source).Resolve(config);
    }

    private static RoleBinding Bind(RoleId role, NetworkAdapter adapter)
        => new() { Role = role, AdapterLuid = adapter.Luid, AdapterGuid = adapter.Guid, LastKnownName = adapter.Name };

    /// <summary>Each app is its own .exe plus the helpers named in <paramref name="extra"/>.</summary>
    private static WfpEnforcer Enforcer(FakeStore store, bool collapse = false, Dictionary<string, string[]>? extra = null)
        => new(store, LoopbackLuid, collapse,
            app => [app.ExecutablePath!, .. extra?.GetValueOrDefault(app.DisplayName) ?? []],
            _ => true);

    private static int Count(FakeStore store, string app) => store.Live.Values.Count(f => f.Name.StartsWith(app + ":"));

    [Fact]
    public void FirstApplyInstallsEverythingInOneTransaction()
    {
        var store = new FakeStore();
        var result = Enforcer(store).Apply(PlanFor(Rule("Alpha"), Rule("Beta")));

        Assert.Equal(1, store.Transactions);
        Assert.Equal(store.Live.Count, result.FiltersAdded);
        Assert.Equal(0, result.FiltersRemoved);
        Assert.Equal(store.Live.Count, result.FiltersInstalled);
        Assert.Equal(2, result.Applied.Count);
        Assert.True(result.Changed);
        Assert.Equal(TimeSpan.FromMilliseconds(7), result.CommitTime);
        Assert.Contains(store.Live.Values, f => f.Name == "Loopback");
    }

    [Fact]
    public void ApplyWithNothingChangedDoesNothingAtAll()
    {
        var store = new FakeStore();
        var enforcer = Enforcer(store);
        var plan = PlanFor(Rule("Alpha"), Rule("Beta"));
        var first = enforcer.Apply(plan);
        store.ResetCounters();
        var transactions = store.Transactions;

        var second = enforcer.Apply(plan);

        Assert.Empty(store.Added);
        Assert.Empty(store.Deleted);
        Assert.Equal(transactions, store.Transactions);
        Assert.False(second.Changed);
        Assert.Equal(0, second.FiltersAdded);
        Assert.Equal(0, second.FiltersRemoved);
        Assert.Equal(TimeSpan.Zero, second.CommitTime);
        Assert.Equal(first.FiltersInstalled, second.FiltersInstalled);
        Assert.Equal(first.Applied.Count, second.Applied.Count);
        Assert.Equal(first.TotalFilters, second.TotalFilters);
    }

    [Fact]
    public void AnEquivalentPlanBuiltAgainIsStillNothingChanged()
    {
        // A new plan object with the same content (as every reconcile produces) must not look like a change.
        var store = new FakeStore();
        var enforcer = Enforcer(store);
        var rule = Rule("Alpha");
        enforcer.Apply(PlanFor(rule));
        store.ResetCounters();

        Assert.False(enforcer.Apply(PlanFor(rule)).Changed);
        Assert.Empty(store.Added);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public void ChangingOneRulesRoleReplacesOnlyThatRulesFilters()
    {
        var store = new FakeStore();
        var enforcer = Enforcer(store);
        var alpha = Rule("Alpha");
        var beta = Rule("Beta");
        enforcer.Apply(PlanFor(alpha, beta));

        var alphaBefore = Count(store, "Alpha");
        var betaIds = store.Live.Where(f => f.Value.Name.StartsWith("Beta:")).Select(f => f.Key).ToHashSet();
        var alwaysBefore = store.Live.Values.Where(f => !f.Name.StartsWith("Alpha:") && !f.Name.StartsWith("Beta:")).ToList();
        store.ResetCounters();

        var result = enforcer.Apply(PlanFor(alpha with { Role = RoleId.Downloads }, beta));

        // Alpha moved to the IPv4-only Downloads network: its permits and blocks are relabelled and re-pointed,
        // and it has IPv6 blocks where it had IPv6 permits. Its two IPv4 loopback permits did not change,
        // so those stay installed untouched.
        Assert.Equal(alphaBefore - 2, result.FiltersRemoved);
        Assert.Equal(alphaBefore - 2, store.Deleted.Count);
        Assert.DoesNotContain(store.Added, f => f.Name.EndsWith("permit loopback"));
        Assert.Contains(store.Live.Values, f => f.Name == "Alpha: permit loopback");
        Assert.All(store.Added, f => Assert.StartsWith("Alpha:", f.Name));
        Assert.Equal(result.FiltersAdded, store.Added.Count);
        Assert.True(store.Added.Count > 0);
        Assert.True(betaIds.IsSubsetOf(store.Live.Keys), "Beta's filters must not be touched");
        Assert.DoesNotContain(store.Deleted, betaIds.Contains);
        Assert.All(alwaysBefore, f => Assert.Contains(f, store.Live.Values));
        Assert.Equal(2, store.Transactions);
    }

    [Fact]
    public void ChangingAnAppsProgramFilesChangesOnlyTheFilesThatMoved()
    {
        var store = new FakeStore();
        var rule = Rule("Alpha");
        var listing = new[] { @"C:\Games\Alpha\helper1.exe", @"C:\Games\Alpha\helper2.exe" };
        var enforcer = new WfpEnforcer(store, LoopbackLuid, false, app => [app.ExecutablePath!, .. listing], _ => true);
        enforcer.Apply(PlanFor(rule));
        var perFile = Count(store, "Alpha") / 3;
        store.ResetCounters();

        listing = [@"C:\Games\Alpha\helper1.exe", @"C:\Games\Alpha\helper3.exe"];
        var result = enforcer.Apply(PlanFor(rule));

        Assert.Equal(perFile, result.FiltersRemoved);
        Assert.Equal(perFile, result.FiltersAdded);
        Assert.All(store.Added, f => Assert.Contains(f.Conditions, c => c is AppIdCondition { Path: @"C:\Games\Alpha\helper3.exe" }));
    }

    [Fact]
    public void RemovingARuleDeletesOnlyItsOwnFilters()
    {
        var store = new FakeStore();
        var enforcer = Enforcer(store);
        var alpha = Rule("Alpha");
        var beta = Rule("Beta");
        enforcer.Apply(PlanFor(alpha, beta));
        var alphaIds = store.Live.Where(f => f.Value.Name.StartsWith("Alpha:")).Select(f => f.Key).ToHashSet();
        store.ResetCounters();

        var result = enforcer.Apply(PlanFor(beta));

        Assert.Equal(alphaIds.Count, result.FiltersRemoved);
        Assert.True(alphaIds.SetEquals(store.Deleted));
        Assert.Empty(store.Added);
        Assert.Equal(0, Count(store, "Alpha"));
        Assert.True(Count(store, "Beta") > 0);
    }

    [Fact]
    public void RemovingTheLastRuleLeavesNoFiltersAtAll()
    {
        var store = new FakeStore();
        var enforcer = Enforcer(store);
        enforcer.Apply(PlanFor(Rule("Alpha")));

        var result = enforcer.Apply(PlanFor());

        Assert.Empty(store.Live);
        Assert.Equal(0, result.FiltersInstalled);
        Assert.Equal(0, enforcer.InstalledFilterCount);
    }

    [Fact]
    public void ARuleThatCannotBeInstalledLeavesTheOthersApplied()
    {
        var store = new FakeStore { FailAdd = f => f.Name.StartsWith("Bad:") && f.Name.Contains("block") };
        var enforcer = Enforcer(store);
        var bad = Rule("Bad");

        var result = enforcer.Apply(PlanFor(Rule("Alpha"), bad, Rule("Beta")));

        var failure = Assert.Single(result.Failed);
        Assert.Equal(bad.Id, failure.Enforcement.Rule.Id);
        Assert.Equal(["Alpha", "Beta"], result.Applied.Select(a => a.Enforcement.Rule.App.DisplayName).Order());
        Assert.Equal(0, Count(store, "Bad"));       // the permits it did get were taken back
        Assert.True(Count(store, "Alpha") > 0);
        Assert.True(Count(store, "Beta") > 0);
        Assert.Contains(store.Live.Values, f => f.Name == "Loopback");
    }

    [Fact]
    public void ARuleThatFailedIsTriedAgainOnTheNextApply()
    {
        var store = new FakeStore { FailAdd = f => f.Name.StartsWith("Bad:") };
        var enforcer = Enforcer(store);
        var plan = PlanFor(Rule("Alpha"), Rule("Bad"));
        enforcer.Apply(plan);
        store.ResetCounters();

        var stillFailing = enforcer.Apply(plan);
        Assert.Single(stillFailing.Failed);
        Assert.DoesNotContain(store.Added, f => f.Name.StartsWith("Alpha:"));

        store.FailAdd = null;
        var recovered = enforcer.Apply(plan);
        Assert.Empty(recovered.Failed);
        Assert.True(Count(store, "Bad") > 0);
    }

    [Fact]
    public void ARuleThatCannotEvenBePlannedIsReportedAndTheOthersStay()
    {
        var store = new FakeStore();
        var enforcer = new WfpEnforcer(store, LoopbackLuid, false, app => app.DisplayName == "Gone" ? [] : [app.ExecutablePath!], _ => true);
        var gone = AppRule.Create(AppIdentity.ForFolder(@"C:\Games\Gone", "Gone"), RoleId.Gaming);

        var result = enforcer.Apply(PlanFor(Rule("Alpha"), gone));

        Assert.Equal(gone.Id, Assert.Single(result.Failed).Enforcement.Rule.Id);
        Assert.Single(result.Applied);
        Assert.True(Count(store, "Alpha") > 0);
        Assert.Equal(0, Count(store, "Gone"));
    }

    [Fact]
    public void ARuleThatStopsPlanningLosesItsOldFiltersRatherThanKeepingStaleOnes()
    {
        var store = new FakeStore();
        var exists = true;
        var enforcer = new WfpEnforcer(store, LoopbackLuid, false, app => exists ? [app.InstallLocation + @"\game.exe"] : [], _ => true);
        var folder = AppRule.Create(AppIdentity.ForFolder(@"C:\Games\Lib", "Lib"), RoleId.Gaming);
        enforcer.Apply(PlanFor(folder));
        Assert.True(Count(store, "Lib") > 0);

        exists = false;
        var result = enforcer.Apply(PlanFor(folder));

        Assert.Single(result.Failed);
        Assert.Equal(0, Count(store, "Lib"));
    }

    [Fact]
    public void AnAbortedTransactionLeavesTheEnforcerRememberingWhatIsReallyInstalled()
    {
        var store = new FakeStore();
        var enforcer = Enforcer(store);
        var alpha = Rule("Alpha");
        enforcer.Apply(PlanFor(alpha));
        var installed = enforcer.InstalledFilterCount;

        store.FailCommit = true;
        Assert.Throws<WfpException>(() => enforcer.Apply(PlanFor(alpha, Rule("Beta"))));
        Assert.Equal(installed, enforcer.InstalledFilterCount);
        Assert.Equal(installed, store.Live.Count);

        store.FailCommit = false;
        store.ResetCounters();
        var plan = PlanFor(alpha, Rule("Beta"));
        enforcer.Apply(plan);
        Assert.True(Count(store, "Beta") > 0);
        Assert.DoesNotContain(store.Added, f => f.Name.StartsWith("Alpha:"));
    }

    [Fact]
    public void EmergencyDisableRemovesEverythingAndTheNextApplyReinstalls()
    {
        var store = new FakeStore();
        var enforcer = Enforcer(store);
        var plan = PlanFor(Rule("Alpha"));
        enforcer.Apply(plan);
        var count = store.Live.Count;

        enforcer.EmergencyDisable();
        Assert.Empty(store.Live);
        Assert.Equal(0, enforcer.InstalledFilterCount);

        Assert.Equal(count, enforcer.Apply(plan).FiltersAdded);
    }

    [Fact]
    public void RulesWithTheSameDisplayNameAndFileDoNotShareFilters()
    {
        var store = new FakeStore();
        var enforcer = Enforcer(store);
        var one = Rule("Same", exe: @"C:\Games\Same\Same.exe");
        var two = Rule("Same", exe: @"C:\Games\Same\Same.exe");
        enforcer.Apply(PlanFor(one, two));
        var both = Count(store, "Same");

        enforcer.Apply(PlanFor(one));

        Assert.Equal(both / 2, Count(store, "Same"));
    }

    [Fact]
    public void WithoutCollapsingEachProgramFileGetsItsOwnFilters()
    {
        var extra = new Dictionary<string, string[]> { ["Alpha"] = [@"C:\Games\Alpha\a.exe", @"C:\Games\Alpha\b.exe"] };
        var split = new FakeStore();
        Enforcer(split, collapse: false, extra).Apply(PlanFor(Rule("Alpha")));
        var single = new FakeStore();
        Enforcer(single, collapse: false).Apply(PlanFor(Rule("Alpha")));

        Assert.Equal(3 * Count(single, "Alpha"), Count(split, "Alpha"));
        Assert.All(split.Live.Values.Where(f => f.Name.StartsWith("Alpha:")),
            f => Assert.Single(f.Conditions, c => c is AppIdCondition));
    }

    [Fact]
    public void CollapsingGivesOneFilterPerLayerHoldingEveryProgramFile()
    {
        var extra = new Dictionary<string, string[]> { ["Alpha"] = [@"C:\Games\Alpha\b.exe", @"C:\Games\Alpha\a.exe"] };
        var store = new FakeStore();
        var one = new FakeStore();
        Enforcer(store, collapse: true, extra).Apply(PlanFor(Rule("Alpha")));
        Enforcer(one, collapse: true).Apply(PlanFor(Rule("Alpha")));

        // Same number of filters as for one program, however many programs there are.
        Assert.Equal(Count(one, "Alpha"), Count(store, "Alpha"));
        var ids = store.Live.Values.Where(f => f.Name.StartsWith("Alpha:"))
            .Select(f => f.Conditions.OfType<AppIdCondition>().Select(c => c.Path).ToArray()).First();
        Assert.Equal(3, ids.Length);
        Assert.Equal(ids.Order(StringComparer.OrdinalIgnoreCase), ids);   // canonical order, whatever the folder listed
    }

    [Fact]
    public void CollapsedFiltersDoNotChurnWhenTheFolderListsFilesInAnotherOrder()
    {
        var store = new FakeStore();
        var order = new[] { @"C:\Games\Alpha\b.exe", @"C:\Games\Alpha\a.exe" };
        var enforcer = new WfpEnforcer(store, LoopbackLuid, true, app => [app.ExecutablePath!, .. order], _ => true);
        var plan = PlanFor(Rule("Alpha"));
        enforcer.Apply(plan);
        store.ResetCounters();

        order = [order[1], order[0]];
        Assert.False(enforcer.Apply(plan).Changed);
    }

    [Fact]
    public void FilterSpecsCompareByValue()
    {
        var layer = Guid.NewGuid();
        FilterSpec Make(string path, ulong luid = 1) => new(layer, 1, 12, "x", [new AppIdCondition(path), new LocalInterfaceCondition(luid)]);

        Assert.Equal(Make(@"C:\A\b.exe"), Make(@"c:\a\B.EXE"));
        Assert.Equal(Make(@"C:\A\b.exe").GetHashCode(), Make(@"c:\a\B.EXE").GetHashCode());
        Assert.NotEqual(Make(@"C:\A\b.exe"), Make(@"C:\A\c.exe"));
        Assert.NotEqual(Make(@"C:\A\b.exe", 1), Make(@"C:\A\b.exe", 2));
        Assert.NotEqual(Make(@"C:\A\b.exe"), Make(@"C:\A\b.exe") with { Weight = 8 });
        Assert.NotEqual(Make(@"C:\A\b.exe"), Make(@"C:\A\b.exe") with { Name = "y" });
    }
}
