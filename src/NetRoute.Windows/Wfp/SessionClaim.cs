namespace NetRoute.Windows.Wfp;

/// <summary>Another process's engine session, as far as ownership reporting needs it.</summary>
internal sealed record OtherSession(uint ProcessId, string? ProcessName)
{
    public override string ToString() => ProcessName is { Length: > 0 } n ? $"{n}, process {ProcessId}" : $"process {ProcessId}";
}

/// <summary>
/// Decides whether the provider and sublayer a new session found already in place are ours.
///
/// <para>The engine reports no owner for an object (the provider and sublayer structures have no
/// session field), so ownership is found by behaviour: a probe filter added into the sublayer and
/// then aborted. A sublayer owned by another live session refuses it with FWP_E_WRONG_SESSION. When
/// it does, either another NetRoute is running (the engine's session list shows a session of the
/// same name in another process), or our own previous session is still tearing down, which settles
/// within moments, so the probe is repeated for a short while before giving up.</para>
/// </summary>
internal static class SessionClaim
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);
    public const int DefaultAttempts = 12;

    /// <param name="probe">Adds a throwaway filter in an aborted transaction; returns the engine's result code.</param>
    /// <param name="otherSessions">Sessions of this identity's name that belong to other processes. May throw; treated as unknown.</param>
    /// <param name="wait">Sleeps between attempts.</param>
    /// <exception cref="AnotherInstanceException">The objects belong to someone else.</exception>
    /// <exception cref="WfpException">The probe failed for a reason other than ownership.</exception>
    public static void Verify(
        Func<uint> probe, Func<IReadOnlyList<OtherSession>> otherSessions, Action<TimeSpan> wait,
        int attempts = DefaultAttempts, TimeSpan? interval = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = probe();
            if (result == 0)
            {
                return;
            }
            if (result != WfpException.FWP_E_WRONG_SESSION)
            {
                throw new WfpException("FwpmFilterAdd0 (ownership probe)", result);
            }

            IReadOnlyList<OtherSession> others;
            try
            {
                others = otherSessions();
            }
            catch (Exception)
            {
                others = [];
            }

            if (others.Count > 0)
            {
                throw new AnotherInstanceException(others.Select(o => o.ToString()).Distinct().ToList());
            }
            if (attempt >= attempts)
            {
                // Held by a session we cannot see for as long as we waited: not ours to wait out.
                throw new AnotherInstanceException([]);
            }
            wait(interval ?? DefaultInterval);
        }
    }
}
