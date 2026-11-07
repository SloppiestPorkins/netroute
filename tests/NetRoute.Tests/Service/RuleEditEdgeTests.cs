using NetRoute.Core.Config;
using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;
using NetRoute.Ipc;
using NetRoute.Service;

namespace NetRoute.Tests.Service;

/// <summary>
/// Edge cases for editing rules through the engine: missing rules, an unreadable config file,
/// and rules that are removed while the engine is running.
/// </summary>
public sealed class RuleEditEdgeTests : IDisposable
{
    private const string Game = @"C:\Games\Halo.exe";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"NetRoute.Edit.{Guid.NewGuid():N}");
    private string ConfigPath => Path.Combine(_directory, "config.json");
    private readonly ManualTime _time = new();
    private readonly NetRouteEngineTests.MutableAdapterSource _adapters =
        new(NetRouteEngineTests.Ethernet(), NetRouteEngineTests.Wifi());

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class ScriptedBackend : IEnforcementBackend
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool RedirectionAvailable => false;
        public BackendApplyResult Apply(EnforcementPlan plan) => BackendApplyResult.Success;
        public void EmergencyDisable() { }
        public void Dispose() { }
    }

    private sealed class FakeDownloads : IDownloadSource
    {
        public IReadOnlyList<DownloadActivityDto> InProgress() => [];
    }

    private NetRouteEngine Engine()
        => new(_adapters, new ScriptedBackend(), ConfigPath, downloads: new FakeDownloads(), time: _time);

    [Fact]
    public async Task EditingARuleThatNoLongerExistsIsRefused()
    {
        using var engine = Engine();
        await engine.CompleteSetupAsync(11, 22);
        var ghost = AppRule.Create(AppIdentity.ForExecutable(Game, "Halo"), RoleId.Gaming);

        await Assert.ThrowsAsync<NetRouteServiceException>(() => engine.UpdateRuleAsync(ghost));
    }

    [Fact]
    public async Task PausingARuleThatNoLongerExistsIsRefused()
    {
        using var engine = Engine();
        await engine.CompleteSetupAsync(11, 22);

        await Assert.ThrowsAsync<NetRouteServiceException>(() => engine.SetRulePausedAsync(Guid.NewGuid(), true));
    }

    /// <summary>Removing a rule that is already gone is harmless, for example from a stale window.</summary>
    [Fact]
    public async Task RemovingARuleTwiceDoesNotThrow()
    {
        using var engine = Engine();
        await engine.CompleteSetupAsync(11, 22);
        await engine.AddRuleAsync(AppIdentity.ForExecutable(Game, "Halo"), RoleId.Gaming);
        var id = (await engine.GetStatusAsync()).Apps.Single().Rule.Id;

        await engine.RemoveRuleAsync(id);
        await engine.RemoveRuleAsync(id);

        Assert.Empty((await engine.GetStatusAsync()).Apps);
    }

    /// <summary>
    /// With the settings file unreadable, an edit must be refused and the file must be left alone.
    /// This is the behaviour that stops a bad file being replaced by an empty one.
    /// </summary>
    [Fact]
    public async Task EditWithUnreadableSettingsIsRefusedAndFileIsKept()
    {
        Directory.CreateDirectory(_directory);
        const string broken = "{ not valid";
        File.WriteAllText(ConfigPath, broken);
        using var engine = Engine();

        var ex = await Assert.ThrowsAsync<NetRouteServiceException>(() =>
            engine.AddRuleAsync(AppIdentity.ForExecutable(Game, "Halo"), RoleId.Gaming));

        Assert.Contains("could not be read", ex.Message);
        Assert.Equal(broken, File.ReadAllText(ConfigPath));
    }

    /// <summary>A rule added after the file was repaired is saved normally.</summary>
    [Fact]
    public async Task EditingWorksAgainOnceTheFileIsRepaired()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, "{ not valid");
        using var engine = Engine();
        await Assert.ThrowsAsync<NetRouteServiceException>(() =>
            engine.AddRuleAsync(AppIdentity.ForExecutable(Game, "Halo"), RoleId.Gaming));

        File.WriteAllText(ConfigPath, "{}");
        await engine.AddRuleAsync(AppIdentity.ForExecutable(Game, "Halo"), RoleId.Gaming);

        Assert.Single(new ConfigStore(ConfigPath).Load().AppRules);
    }
}
