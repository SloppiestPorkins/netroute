using System.Runtime.InteropServices;
using static NetRoute.Windows.Wfp.WfpNative;

namespace NetRoute.Windows.Wfp;

/// <summary>
/// The filter engine as the enforcer needs it: transactions, and adding or deleting one filter.
/// A seam so the diff logic can be tested without administrator rights or a real engine.
/// </summary>
internal interface IFilterStore
{
    /// <summary>Runs <paramref name="action"/> so its adds and deletes all apply or none do.</summary>
    void InTransaction(Action action);

    /// <summary>Installs one filter and returns its engine id.</summary>
    ulong Add(FilterSpec spec);

    /// <summary>Removes one filter. One that has already gone is not an error.</summary>
    void Delete(ulong id);

    /// <summary>
    /// The ids of every filter in the engine filed under this store's provider, including any the
    /// enforcer has lost track of. A store that cannot tell returns nothing.
    /// </summary>
    IReadOnlyList<ulong> EnumerateOwned() => [];

    /// <summary>How long the last transaction spent in its commit.</summary>
    TimeSpan LastCommitDuration { get; }
}

/// <summary>The real engine, through a <see cref="WfpSession"/>.</summary>
internal sealed class WfpFilterStore(WfpSession session) : IFilterStore
{
    public TimeSpan LastCommitDuration => session.LastCommitDuration;

    public void InTransaction(Action action) => session.InTransaction(action);

    public IReadOnlyList<ulong> EnumerateOwned() => session.EnumerateFilterIds(session.Identity.ProviderKey);

    public void Delete(ulong id)
    {
        var result = FwpmFilterDeleteById0(session.Handle, id);
        if (result != 0 && result != WfpException.FWP_E_NOT_FOUND)
        {
            throw new WfpException("FwpmFilterDeleteById0", result);
        }
    }

    public ulong Add(FilterSpec spec)
    {
        var providerKeyPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());

        // The scope owns every buffer the conditions point at, and it is disposed only after
        // FwpmFilterAdd0 has returned: the engine reads the blobs during the call and keeps its own copy.
        using var scope = new ConditionScope();
        try
        {
            Marshal.StructureToPtr(session.Identity.ProviderKey, providerKeyPtr, false);

            var conditions = spec.Conditions.Select(c => Build(scope, c)).ToList();
            var filter = new FWPM_FILTER0
            {
                filterKey = Guid.NewGuid(),
                displayData = new FWPM_DISPLAY_DATA0 { name = spec.Name, description = "NetRoute policy" },
                providerKey = providerKeyPtr,
                layerKey = spec.Layer,
                subLayerKey = session.Identity.SubLayerKey,

                // UINT8 weights are stored inline and give 16 relative levels within the
                // sublayer, which is ample here and avoids managing a pinned UINT64.
                weight = new FWP_VALUE0 { type = FwpDataType.Uint8, value = (IntPtr)spec.Weight },
                numFilterConditions = (uint)conditions.Count,
                filterCondition = scope.MarshalConditions(conditions),
                action = new FWPM_ACTION0 { type = spec.Action }
            };

            WfpException.ThrowIfFailed($"FwpmFilterAdd0({spec.Name})", FwpmFilterAdd0(session.Handle, ref filter, IntPtr.Zero, out var id));
            return id;
        }
        finally
        {
            Marshal.FreeHGlobal(providerKeyPtr);
        }
    }

    private static FWPM_FILTER_CONDITION0 Build(ConditionScope scope, ConditionSpec spec) => spec switch
    {
        AppIdCondition c => scope.AppId(c.Path),
        PackageCondition c => scope.PackageId(c.FamilyName),
        ServiceCondition c => scope.ServiceUser(c.ServiceName),
        LoopbackFlagsCondition => scope.LoopbackFlags(),
        RemoteRangeCondition c => scope.RemoteRange(c.Network, c.PrefixLength),
        LocalInterfaceCondition c => scope.LocalInterface(c.Luid),
        ProtocolCondition c => scope.Protocol(c.Protocol),
        _ => throw new ArgumentOutOfRangeException(nameof(spec), spec, "Unknown condition")
    };
}
