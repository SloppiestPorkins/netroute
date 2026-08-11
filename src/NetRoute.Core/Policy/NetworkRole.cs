namespace NetRoute.Core.Policy;

/// <summary>
/// The logical destinations a user assigns applications to.
///
/// <para>This is the indirection that §3 and §33 are built on. Application rules
/// reference a role, never an adapter. Repointing Gaming from Ethernet to Wi-Fi
/// moves every Gaming app with it, and leaves Downloads apps exactly where they were.</para>
/// </summary>
public enum RoleId
{
    /// <summary>NetRoute does not override Windows routing. Not backed by an adapter.</summary>
    Default = 0,

    Gaming = 1,
    Downloads = 2
}

public static class Role
{
    public static string DisplayName(this RoleId role) => role switch
    {
        RoleId.Gaming => "Gaming",
        RoleId.Downloads => "Downloads",
        RoleId.Default => "Default",
        _ => role.ToString()
    };

    public static string Glyph(this RoleId role) => role switch
    {
        RoleId.Gaming => "🎮",
        RoleId.Downloads => "⬇",
        RoleId.Default => "🌐",
        _ => "•"
    };

    /// <summary>Roles the user actually binds to an adapter. Default is deliberately excluded.</summary>
    public static IReadOnlyList<RoleId> Bindable { get; } = new[] { RoleId.Gaming, RoleId.Downloads };
}

/// <summary>
/// A role's persisted pointer at an adapter.
///
/// <para>Stores the LUID, not the friendly name or IP (§7). <see cref="LastKnownName"/>
/// exists only so the UI can say "Ethernet is missing" instead of "0x0006008001000000
/// is missing" when the adapter is gone; it is never used to find the adapter.</para>
/// </summary>
public sealed record RoleBinding
{
    public required RoleId Role { get; init; }
    public required ulong AdapterLuid { get; init; }

    /// <summary>Secondary identity, used only if LUID lookup fails.</summary>
    public string? AdapterGuid { get; init; }

    /// <summary>Display fallback for error messages. Never used for resolution.</summary>
    public string? LastKnownName { get; init; }
}
