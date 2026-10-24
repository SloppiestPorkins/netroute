using System.ComponentModel;

namespace NetRoute.Windows.Wfp;

/// <summary>
/// A WFP call failed.
///
/// <para>§31 requires that raw Windows errors never be the primary message shown to a
/// user, so this type carries both: <see cref="FriendlyMessage"/> for the UI and the
/// exact code for Advanced Details.</para>
/// </summary>
public sealed class WfpException : Exception
{
    public uint ErrorCode { get; }
    public string Operation { get; }

    public WfpException(string operation, uint errorCode)
        : base($"{operation} failed with {Describe(errorCode)} (0x{errorCode:X8}).")
    {
        Operation = operation;
        ErrorCode = errorCode;
    }

    /// <summary>What this failure means for the user's protection, appended to <see cref="FriendlyMessage"/>.</summary>
    public string? Consequence { get; init; }

    /// <summary>The same failure, with what it leaves unprotected spelled out.</summary>
    public WfpException WithConsequence(string consequence) => new(Operation, ErrorCode) { Consequence = consequence };

    /// <summary>Plain-language explanation suitable for showing directly to a user.</summary>
    public string FriendlyMessage => Consequence is null ? Reason : $"{Reason} {Consequence}";

    private string Reason => ErrorCode switch
    {
        FWP_E_ALREADY_EXISTS => "This rule already exists.",
        ERROR_ACCESS_DENIED => "NetRoute needs administrator rights to change network policy.",
        FWP_E_NOT_FOUND => "The rule NetRoute tried to change no longer exists.",
        FWP_E_TIMEOUT => "Windows did not respond in time. Try again.",
        FWP_E_INVALID_PARAMETER => "NetRoute could not apply this rule to your network configuration.",
        FWP_E_WRONG_SESSION => "Another copy of NetRoute is already managing network policy.",
        _ => "We couldn't apply this rule."
    };

    public const uint ERROR_ACCESS_DENIED = 5;
    public const uint FWP_E_ALREADY_EXISTS = 0x80320009;
    public const uint FWP_E_NOT_FOUND = 0x80320008;
    public const uint FWP_E_TIMEOUT = 0x80320005;
    public const uint FWP_E_INVALID_PARAMETER = 0x80320035;

    /// <summary>The object being changed belongs to another session (typically another live NetRoute).</summary>
    public const uint FWP_E_WRONG_SESSION = 0x8032000C;
    public const uint RPC_S_SERVER_UNAVAILABLE = 1722;
    public const uint EPT_S_NOT_REGISTERED = 1753;

    private static string Describe(uint code) => code switch
    {
        ERROR_ACCESS_DENIED => "ERROR_ACCESS_DENIED",
        FWP_E_ALREADY_EXISTS => "FWP_E_ALREADY_EXISTS",
        FWP_E_NOT_FOUND => "FWP_E_NOT_FOUND",
        FWP_E_TIMEOUT => "FWP_E_TIMEOUT",
        FWP_E_INVALID_PARAMETER => "FWP_E_INVALID_PARAMETER",
        FWP_E_WRONG_SESSION => "FWP_E_WRONG_SESSION",
        _ => new Win32Exception((int)code).Message
    };

    internal static void ThrowIfFailed(string operation, uint result)
    {
        if (result != 0)
        {
            throw new WfpException(operation, result);
        }
    }
}
