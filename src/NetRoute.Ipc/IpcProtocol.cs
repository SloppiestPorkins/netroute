using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRoute.Ipc;

/// <summary>
/// Wire protocol between the GUI/CLI and the NetRoute service (§42).
///
/// <para>Newline-delimited JSON over a local named pipe. Each line a client writes is
/// one <see cref="IpcRequest"/>; the service answers each with exactly one
/// <see cref="IpcResponse"/> line, in order. A connection may carry any number of
/// requests. Plain text keeps the protocol debuggable with nothing more than a pipe
/// client, which matters for something whose failure mode is "my game has no internet".</para>
/// </summary>
public static class IpcProtocol
{
    /// <summary>Versioned so an old GUI talking to a new service fails clearly instead of oddly.</summary>
    public const string PipeName = "NetRoute.Service.v1";

    public const int Version = 1;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>Command names. Strings rather than an enum so unknown commands round-trip as a clear error.</summary>
public static class IpcCommands
{
    public const string Ping = "ping";
    public const string GetStatus = "getStatus";
    public const string GetAdapters = "getAdapters";
    public const string GetConnections = "getConnections";
    public const string CompleteSetup = "completeSetup";
    public const string SetRoleAdapter = "setRoleAdapter";
    public const string AddRule = "addRule";
    public const string UpdateRule = "updateRule";
    public const string RemoveRule = "removeRule";
    public const string SetRulePaused = "setRulePaused";
    public const string SetEnforcementPaused = "setEnforcementPaused";
    public const string EmergencyDisable = "emergencyDisable";
}

public sealed record IpcRequest
{
    public required string Command { get; init; }
    public JsonElement? Payload { get; init; }
}

public sealed record IpcResponse
{
    public required bool Ok { get; init; }
    public JsonElement? Payload { get; init; }
    public IpcError? Error { get; init; }
}

/// <summary>
/// A failure as the user should see it, plus the raw detail for Advanced Details (§31).
/// </summary>
public sealed record IpcError
{
    /// <summary>Plain language, safe to show as the primary message.</summary>
    public required string FriendlyMessage { get; init; }

    /// <summary>Exact error, e.g. "FwpmFilterAdd0 failed with FWP_E_INVALID_PARAMETER (0x80320035)".</summary>
    public string? TechnicalDetail { get; init; }
}

/// <summary>Thrown by clients when the service is not running or not reachable.</summary>
public sealed class ServiceUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Thrown by clients when the service handled the request but it failed.</summary>
public sealed class NetRouteServiceException(IpcError error)
    : Exception(error.TechnicalDetail ?? error.FriendlyMessage)
{
    public IpcError Error { get; } = error;
}
