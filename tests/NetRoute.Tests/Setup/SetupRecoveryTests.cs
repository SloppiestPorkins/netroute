using NetRoute.Setup;

namespace NetRoute.Tests.Setup;

/// <summary>
/// The rules that decide what setup leaves behind when something goes wrong.
///
/// <para>These are the parts of setup that can be checked without a machine to damage, which is
/// why they were pulled out as pure logic. They come from the review of 23 October 2026:
/// a failed fresh install was not rolled back at all (5), an update's rollback never gave the old
/// service its restart settings back (5), a removal deleted the only program that could put
/// Windows' network settings back even when the restore had failed (6), and a quiet run reported
/// success before any work had happened (4).</para>
/// </summary>
public class SetupRecoveryTests
{
    // ---- what a rollback undoes (finding 5) ----

    [Fact]
    public void A_failed_fresh_install_leaves_nothing_behind()
    {
        var plan = RollbackPlan.For(new RollbackFacts
        {
            HadInstall = false, FilesChanged = true, ServiceCreated = true, DriverCreated = true, DaemonsTurnedOff = true
        });

        // No previous version to go back to, so every trace of the attempt goes.
        Assert.Contains(RollbackStep.DeleteNewFiles, plan);
        Assert.Contains(RollbackStep.DeleteServiceWeCreated, plan);
        Assert.Contains(RollbackStep.RemoveDriverWeCreated, plan);
        Assert.Contains(RollbackStep.ReenableMullvadDaemons, plan);
        Assert.Contains(RollbackStep.ForgetSetupState, plan);
        Assert.DoesNotContain(RollbackStep.RestoreOldFiles, plan);
    }

    [Fact]
    public void A_failed_update_puts_the_old_version_back_and_starts_it()
    {
        var plan = RollbackPlan.For(new RollbackFacts
        {
            HadInstall = true, FilesChanged = true, BackupExists = true, ServiceWasRunning = true
        });

        Assert.Contains(RollbackStep.RestoreOldFiles, plan);
        Assert.Contains(RollbackStep.RestartOldService, plan);
        // An update inherits the driver and the Mullvad switch-off from the install it replaces.
        Assert.DoesNotContain(RollbackStep.RemoveDriverWeCreated, plan);
        Assert.DoesNotContain(RollbackStep.ReenableMullvadDaemons, plan);
        Assert.DoesNotContain(RollbackStep.DeleteNewFiles, plan);
    }

    [Fact]
    public void A_failed_update_always_gives_the_old_service_its_restart_settings_back()
    {
        // Stopping the service strips its recovery actions so it cannot restart mid-copy. Only a
        // completed install put them back, so a failure used to leave the old service unable to
        // restart itself -- whether or not a single file had been touched.
        var untouched = RollbackPlan.For(new RollbackFacts { HadInstall = true });
        var touched = RollbackPlan.For(new RollbackFacts { HadInstall = true, FilesChanged = true, BackupExists = true });

        Assert.Contains(RollbackStep.RestoreRecoveryActions, untouched);
        Assert.Contains(RollbackStep.RestoreRecoveryActions, touched);
    }

    [Fact]
    public void Files_are_put_back_before_the_service_is_restarted()
    {
        var plan = RollbackPlan.For(new RollbackFacts
        {
            HadInstall = true, FilesChanged = true, BackupExists = true, ServiceWasRunning = true
        });

        Assert.True(plan.IndexOf(RollbackStep.StopNewService) < plan.IndexOf(RollbackStep.RestoreOldFiles));
        Assert.True(plan.IndexOf(RollbackStep.RestoreOldFiles) < plan.IndexOf(RollbackStep.RestartOldService));
    }

    [Fact]
    public void A_fresh_install_that_failed_before_copying_anything_has_nothing_to_undo()
        => Assert.Equal([RollbackStep.ForgetSetupState], RollbackPlan.For(new RollbackFacts { HadInstall = false }));

    // ---- whether Windows' settings were really put back (finding 6) ----

    // The outcome is named rather than passed as the enum, because the rules it comes from are
    // internal to setup and a public test method cannot take an internal type.
    [Theory]
    // The program ran, said it worked, and cleared the record it had just used.
    [InlineData(true, true, 0, false, "Restored")]
    // It said it worked but the record is still there, so it did not finish. Its word is not enough.
    [InlineData(true, true, 0, true, "Failed")]
    [InlineData(true, true, 1, true, "Failed")]
    // No program to run it, but there was something saved: nothing can undo it.
    [InlineData(false, true, null, true, "Failed")]
    // Nothing was ever saved, so there is nothing to put back.
    [InlineData(false, false, null, false, "NothingToRestore")]
    public void A_restore_counts_only_on_evidence(
        bool cliExists, bool stateBefore, int? exitCode, bool stateAfter, string expected)
        => Assert.Equal(expected, RestorePolicy.Decide(cliExists, stateBefore, exitCode, stateAfter).ToString());

    [Fact]
    public void A_failed_restore_stops_the_removal_unless_the_person_insists()
    {
        Assert.False(RestorePolicy.MayContinue(RestoreOutcome.Failed, force: false));
        Assert.True(RestorePolicy.MayContinue(RestoreOutcome.Failed, force: true));
        Assert.True(RestorePolicy.MayContinue(RestoreOutcome.Restored, force: false));
        Assert.True(RestorePolicy.MayContinue(RestoreOutcome.NothingToRestore, force: false));
    }

    [Fact]
    public void The_manual_instructions_say_where_the_record_was_kept_and_what_to_run()
    {
        var advice = RestorePolicy.ManualInstructions(@"C:\ProgramData\NetRoute\route-state.json");

        Assert.Contains(@"C:\ProgramData\NetRoute\route-state.json", advice);
        Assert.Contains("AutomaticMetric Enabled", advice);
    }

    // ---- what the exit code means (finding 4) ----

    [Fact]
    public void A_removal_that_could_not_restore_the_settings_is_not_a_success()
    {
        Assert.Equal(SetupExit.Failed, SetupExit.For(succeeded: true, restartNeeded: false, incomplete: true));
        // Even with a restart pending: a script must not read 3010 as "it worked".
        Assert.Equal(SetupExit.Failed, SetupExit.For(succeeded: true, restartNeeded: true, incomplete: true));
    }

    [Fact]
    public void An_ordinary_run_still_reports_what_it_always_did()
    {
        Assert.Equal(SetupExit.Success, SetupExit.For(succeeded: true, restartNeeded: false));
        Assert.Equal(SetupExit.RestartNeeded, SetupExit.For(succeeded: true, restartNeeded: true));
        Assert.Equal(SetupExit.Failed, SetupExit.For(succeeded: false, restartNeeded: false));
    }

    [Fact]
    public void Handing_the_work_to_a_copy_reports_that_copys_result_and_never_a_silent_success()
    {
        Assert.Equal(SetupExit.Success, SetupExit.ForChild(started: true, childExitCode: 0));
        Assert.Equal(SetupExit.RestartNeeded, SetupExit.ForChild(started: true, childExitCode: SetupExit.RestartNeeded));
        Assert.Equal(SetupExit.Failed, SetupExit.ForChild(started: true, childExitCode: 1));
        // The copy could not be started, or its code could not be read.
        Assert.Equal(SetupExit.Failed, SetupExit.ForChild(started: false, childExitCode: null));
        Assert.Equal(SetupExit.Failed, SetupExit.ForChild(started: true, childExitCode: null));
    }

    // ---- the uninstaller's own file ----

    [Fact]
    public void Only_the_uninstallers_own_file_is_left_for_a_script()
    {
        const string uninstaller = @"C:\Program Files\NetRoute\Uninstall NetRoute.exe";

        Assert.True(SelfDelete.IsLeftToWaitingParent(uninstaller, uninstaller));
        Assert.True(SelfDelete.IsLeftToWaitingParent(uninstaller.ToUpperInvariant(), uninstaller));
        // The driver is the other file that can be in use, and that one does need a restart.
        Assert.False(SelfDelete.IsLeftToWaitingParent(@"C:\Program Files\NetRoute\driver\mullvad-split-tunnel.sys", uninstaller));
    }

    [Fact]
    public void The_tidy_up_script_removes_the_file_the_folder_and_itself()
    {
        var script = SelfDelete.Script(
            @"C:\Program Files\NetRoute\Uninstall NetRoute.exe",
            @"C:\Program Files\NetRoute",
            @"C:\Temp\tidy.cmd");

        Assert.Contains(@"del /f /q ""C:\Program Files\NetRoute\Uninstall NetRoute.exe""", script);
        Assert.Contains(@"rmdir ""C:\Program Files\NetRoute""", script);
        Assert.Contains(@"del /f /q ""C:\Temp\tidy.cmd""", script);
        // It has to give up rather than spin for ever if the file never becomes deletable.
        Assert.Contains("if %tries% geq 60 goto end", script);
    }
}
