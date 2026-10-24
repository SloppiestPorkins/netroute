using NetRoute.Core.Adapters;
using NetRoute.Core.Policy;

namespace NetRoute.Windows.Wfp;

/// <summary>
/// Keeps the WFP filter engine matching an <see cref="EnforcementPlan"/>.
///
/// <para>What the plan calls for is worked out first as plain data (see <see cref="FilterPlanner"/>),
/// then compared with what this enforcer has already installed. Only the difference reaches the
/// engine: filters that are gone are deleted, filters that are new are added, and everything else
/// is left running. An apply where nothing changed does not open a transaction at all.</para>
///
/// <para>This replaced a rebuild-everything apply, which deleted and re-added every filter (about
/// 1500 on the owner's machine) inside one transaction on every reconcile. The commit of a
/// transaction that size is where the filter engine takes its lock and swaps the set in, which is
/// when classification of new connections can stall.</para>
/// </summary>
public sealed class WfpEnforcer
{
    /// <summary>
    /// Whether a rule covering several program files gets one filter per layer with an
    /// <c>ALE_APP_ID</c> condition for each file, rather than one filter per file.
    ///
    /// <para>That is only correct if WFP ORs conditions on the same field within a filter, which
    /// is what Microsoft's documentation says. It is off until <c>netroute-poc wfp-or</c> has
    /// shown it on the machine that will run it: a real connection from each of two programs in
    /// one such filter, and one from a program outside it. If same-field conditions ANDed instead,
    /// a collapsed filter would match no program at all and silently protect nothing.</para>
    /// </summary>
    public const bool CollapseProgramFilesByDefault = false;

    private readonly IFilterStore _store;
    private readonly ulong? _loopbackLuid;
    private readonly bool _collapse;
    private readonly Func<AppIdentity, IReadOnlyList<string>>? _programFiles;
    private readonly Func<string, bool>? _fileExists;

    // What is installed, by owner, with the description of each filter next to its engine id.
    // Only replaced once a transaction has committed, so it never describes an aborted one.
    private Dictionary<string, Dictionary<FilterSpec, ulong>> _installed = [];

    public WfpEnforcer(WfpSession session, AdapterDiscovery discovery, bool? collapseProgramFiles = null)
        : this(new WfpFilterStore(session),
            discovery.DiscoverAll().FirstOrDefault(a => a.Kind == AdapterKind.Loopback)?.Luid,
            collapseProgramFiles ?? CollapseProgramFilesByDefault)
    {
    }

    internal WfpEnforcer(
        IFilterStore store, ulong? loopbackLuid, bool collapseProgramFiles,
        Func<AppIdentity, IReadOnlyList<string>>? programFiles = null, Func<string, bool>? fileExists = null)
    {
        _store = store;
        _loopbackLuid = loopbackLuid;
        _collapse = collapseProgramFiles;
        _programFiles = programFiles;
        _fileExists = fileExists;
    }

    public int InstalledFilterCount => _installed.Values.Sum(o => o.Count);

    /// <summary>
    /// Brings the engine in line with <paramref name="plan"/>, changing only what differs.
    ///
    /// <para>The changes are made in one transaction so policy never exists half-applied, which also
    /// gives §19 its behaviour: changing an application's role takes effect for new connections
    /// immediately, without restarting NetRoute. One rule that cannot be enforced is reported and
    /// does not stop the others.</para>
    /// </summary>
    public EnforcementResult Apply(EnforcementPlan plan)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var groups = FilterPlanner.Build(plan, _loopbackLuid, _collapse, _programFiles, _fileExists);

        var failed = new List<FailedRule>();
        WfpException? systemError = null;
        foreach (var group in groups.Where(g => g.Error is not null && g.App is not null))
        {
            failed.Add(new FailedRule(group.App!, NotProtected(group.App!, group.Error!)));
        }

        // Work on a copy: the real state moves only if the transaction commits.
        var working = _installed.ToDictionary(o => o.Key, o => new Dictionary<FilterSpec, ulong>(o.Value));
        var desiredOwners = groups.Select(g => g.Owner).ToHashSet();

        var deletes = new List<ulong>();
        foreach (var owner in working.Keys.Where(o => !desiredOwners.Contains(o)).ToList())
        {
            deletes.AddRange(working[owner].Values);
            working.Remove(owner);
        }

        var adds = new List<(FilterGroup Group, List<FilterSpec> Specs)>();
        foreach (var group in groups)
        {
            var have = working.GetValueOrDefault(group.Owner) ?? [];
            var want = group.Filters.ToHashSet();

            foreach (var (spec, id) in have.Where(h => !want.Contains(h.Key)).ToList())
            {
                deletes.Add(id);
                have.Remove(spec);
            }

            var missing = group.Filters.Where(s => !have.ContainsKey(s)).ToList();
            if (missing.Count > 0)
            {
                adds.Add((group, missing));
            }
            working[group.Owner] = have;
        }

        var added = 0;
        var removed = deletes.Count;
        var rolledBack = new HashSet<string>();
        var transacted = deletes.Count > 0 || adds.Count > 0;

        if (transacted)
        {
            _store.InTransaction(() =>
            {
                foreach (var id in deletes)
                {
                    _store.Delete(id);
                }

                foreach (var (group, specs) in adds)
                {
                    var made = new List<(FilterSpec Spec, ulong Id)>();
                    try
                    {
                        foreach (var spec in specs)
                        {
                            made.Add((spec, _store.Add(spec)));
                        }
                    }
                    catch (WfpException ex)
                    {
                        // Take back what this rule got so far, so it is not left half-installed.
                        foreach (var (_, id) in made)
                        {
                            try
                            {
                                _store.Delete(id);
                            }
                            catch (WfpException)
                            {
                            }
                        }

                        rolledBack.Add(group.Owner);
                        if (group.App is { } app)
                        {
                            failed.Add(new FailedRule(app, NotProtected(app, ex)));
                        }
                        else if (group.IsSystemDownloads)
                        {
                            systemError = ex;
                        }
                        // The always-on permits failing just means local traffic follows each app's rule, as it used to.
                        continue;
                    }

                    foreach (var (spec, id) in made)
                    {
                        working[group.Owner][spec] = id;
                    }
                    added += made.Count;
                }
            });
        }

        _installed = working.Where(o => o.Value.Count > 0).ToDictionary(o => o.Key, o => o.Value);

        var applied = groups
            .Where(g => g.App is not null && g.Error is null && g.Filters.Count > 0 && !rolledBack.Contains(g.Owner))
            .Select(g => new AppliedRule(g.App!, g.Filters.Count))
            .ToList();

        return new EnforcementResult(applied, failed, systemError)
        {
            FiltersRemoved = removed,
            FiltersAdded = added,
            FiltersInstalled = InstalledFilterCount,
            Elapsed = timer.Elapsed,
            CommitTime = transacted ? _store.LastCommitDuration : TimeSpan.Zero
        };
    }

    /// <summary>
    /// A rule with no filters installed enforces nothing, so say that, rather than only that something failed.
    /// The engine retries with backoff, and the message says so.
    /// </summary>
    private static WfpException NotProtected(AppEnforcement app, WfpException error)
        => error.WithConsequence(app.Action == EnforcementAction.BlockAll
            ? "This app is NOT being blocked right now: none of its filters are installed. NetRoute will keep retrying."
            : "This app is NOT protected right now: none of its filters are installed, so nothing keeps it off the wrong network. NetRoute will keep retrying.");

    /// <summary>Why the last <see cref="EmergencyDisable"/> could not list NetRoute's filters in the engine, or null if it could.</summary>
    public WfpException? LastEnumerationError { get; private set; }

    /// <summary>
    /// Removes every NetRoute filter, restoring normal Windows networking (§43).
    ///
    /// <para>What is removed is what this enforcer remembers plus every filter the engine lists under
    /// NetRoute's provider key, so filters an earlier apply committed but this object lost track of
    /// (an exception between commit and bookkeeping) are not skipped. If the engine cannot be
    /// listed, the remembered filters are still removed and the reason is kept in
    /// <see cref="LastEnumerationError"/>; failing to look must not stop an emergency disable.</para>
    /// </summary>
    public void EmergencyDisable()
    {
        var ids = new HashSet<ulong>(_installed.Values.SelectMany(o => o.Values));
        LastEnumerationError = null;
        try
        {
            foreach (var id in _store.EnumerateOwned())
            {
                ids.Add(id);
            }
        }
        catch (WfpException ex)
        {
            LastEnumerationError = ex;
        }

        if (ids.Count == 0)
        {
            _installed = [];
            return;
        }

        _store.InTransaction(() =>
        {
            foreach (var id in ids)
            {
                _store.Delete(id);
            }
        });
        _installed = [];
    }
}

public sealed record AppliedRule(AppEnforcement Enforcement, int FilterCount);

public sealed record FailedRule(AppEnforcement Enforcement, WfpException Error);

public sealed record EnforcementResult(
    IReadOnlyList<AppliedRule> Applied,
    IReadOnlyList<FailedRule> Failed,
    WfpException? SystemDownloadsError = null)
{
    /// <summary>
    /// What the last apply did to the engine, for the service's log: filters actually deleted and
    /// actually added. Both are zero for an apply that found nothing to change, which also opens
    /// no transaction.
    /// </summary>
    public int FiltersRemoved { get; init; }
    public int FiltersAdded { get; init; }

    /// <summary>How many NetRoute filters are installed after this apply.</summary>
    public int FiltersInstalled { get; init; }

    /// <summary>The whole apply, and the transaction commit within it (zero when there was no transaction).</summary>
    public TimeSpan Elapsed { get; init; }
    public TimeSpan CommitTime { get; init; }

    /// <summary>Whether this apply changed anything in the engine.</summary>
    public bool Changed => FiltersAdded > 0 || FiltersRemoved > 0;

    public int TotalFilters => Applied.Sum(a => a.FilterCount);
    public bool FullySucceeded => Failed.Count == 0;
}
