using System.Collections.Generic;

namespace NetRoute.Setup;

/// <summary>One thing a rollback does, in the order it does it.</summary>
internal enum RollbackStep
{
    StopNewService,
    RestoreOldFiles,
    DeleteNewFiles,
    DeleteServiceWeCreated,
    RemoveDriverWeCreated,
    ReenableMullvadDaemons,
    ForgetSetupState,
    RestoreRecoveryActions,
    RestartOldService
}

/// <summary>What this run did before it failed, which is all a rollback needs to know.</summary>
internal sealed class RollbackFacts
{
    /// <summary>NetRoute's program was installed before this run began.</summary>
    public bool HadInstall;
    /// <summary>Setup had started copying into the install folder.</summary>
    public bool FilesChanged;
    /// <summary>A copy of the previous install exists to put back.</summary>
    public bool BackupExists;
    /// <summary>This run registered the NetRoute service (it was not there before).</summary>
    public bool ServiceCreated;
    /// <summary>This run registered the driver service (it was not there before).</summary>
    public bool DriverCreated;
    /// <summary>This run turned off Mullvad VPN services.</summary>
    public bool DaemonsTurnedOff;
    /// <summary>The NetRoute service was running when this run began.</summary>
    public bool ServiceWasRunning;
}

/// <summary>
/// The rule for undoing a failed install or update: leave either the old version working or a
/// clean machine. Pure, so the rule can be checked without a machine to damage.
/// </summary>
internal static class RollbackPlan
{
    public static List<RollbackStep> For(RollbackFacts f)
    {
        var plan = new List<RollbackStep>();
        var fresh = !f.HadInstall;

        if (f.FilesChanged || f.ServiceCreated)
        {
            plan.Add(RollbackStep.StopNewService);
        }
        if (f.FilesChanged && f.BackupExists)
        {
            plan.Add(RollbackStep.RestoreOldFiles);
        }
        else if (f.FilesChanged && fresh)
        {
            plan.Add(RollbackStep.DeleteNewFiles);
        }
        if (f.ServiceCreated)
        {
            plan.Add(RollbackStep.DeleteServiceWeCreated);
        }
        if (fresh)
        {
            // Only a fresh install owns the driver registration and the Mullvad switch-off outright;
            // an update inherits both from the install it is replacing and leaves them as they were.
            if (f.DriverCreated)
            {
                plan.Add(RollbackStep.RemoveDriverWeCreated);
            }
            if (f.DaemonsTurnedOff)
            {
                plan.Add(RollbackStep.ReenableMullvadDaemons);
            }
            plan.Add(RollbackStep.ForgetSetupState);
        }
        else
        {
            // The old service had its recovery actions stripped to stop it; give them back
            // whether or not any file was touched.
            plan.Add(RollbackStep.RestoreRecoveryActions);
            if (f.ServiceWasRunning)
            {
                plan.Add(RollbackStep.RestartOldService);
            }
        }
        return plan;
    }
}
