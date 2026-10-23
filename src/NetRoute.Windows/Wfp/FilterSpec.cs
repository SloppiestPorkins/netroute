using System.Net;

namespace NetRoute.Windows.Wfp;

/// <summary>
/// One filter condition as plain data: what to match, with no native memory behind it. The
/// enforcer compares these to decide what is already installed; <see cref="WfpFilterStore"/>
/// turns them into <c>FWPM_FILTER_CONDITION0</c> only for a filter it is actually adding.
/// </summary>
internal abstract record ConditionSpec;

/// <summary>A program file. Windows paths are case-insensitive, so equality is too.</summary>
internal sealed record AppIdCondition(string Path) : ConditionSpec
{
    public bool Equals(AppIdCondition? other)
        => other is not null && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Path);
}

internal sealed record PackageCondition(string FamilyName) : ConditionSpec;

internal sealed record ServiceCondition(string ServiceName) : ConditionSpec;

internal sealed record LoopbackFlagsCondition : ConditionSpec;

internal sealed record RemoteRangeCondition(IPAddress Network, int PrefixLength) : ConditionSpec;

internal sealed record LocalInterfaceCondition(ulong Luid) : ConditionSpec;

internal sealed record ProtocolCondition(byte Protocol) : ConditionSpec;

/// <summary>
/// Everything that distinguishes one WFP filter from another, as a value: two filters with equal
/// specs do the same thing, so an apply that wants a spec which is already installed leaves it
/// alone. The label is part of the identity so that renaming an app still shows up in the
/// engine's own filter list.
/// </summary>
internal sealed record FilterSpec(
    Guid Layer, uint Action, byte Weight, string Name, IReadOnlyList<ConditionSpec> Conditions)
{
    public bool Equals(FilterSpec? other)
        => other is not null
           && Layer == other.Layer
           && Action == other.Action
           && Weight == other.Weight
           && string.Equals(Name, other.Name, StringComparison.Ordinal)
           && Conditions.SequenceEqual(other.Conditions);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Layer);
        hash.Add(Action);
        hash.Add(Weight);
        hash.Add(Name, StringComparer.Ordinal);
        foreach (var condition in Conditions)
        {
            hash.Add(condition);
        }
        return hash.ToHashCode();
    }
}
