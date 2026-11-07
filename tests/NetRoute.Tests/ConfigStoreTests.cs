using NetRoute.Core.Config;
using NetRoute.Core.Policy;
using Xunit;

namespace NetRoute.Tests;

/// <summary>
/// Regression tests for the config store. Each test gets its own temporary folder so they can run in parallel.
/// </summary>
public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netroute-config-tests-" + Guid.NewGuid().ToString("N"));

    public ConfigStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string PathFor(string name = "config.json") => Path.Combine(_dir, name);

    /// <summary>
    /// A config file that will not parse must never be replaced by an empty config. Before the fix,
    /// the next rule edit wrote an empty config over it and the rules were gone.
    /// </summary>
    [Fact]
    public void UpdateRefusesToOverwriteAnUnreadableFile()
    {
        var path = PathFor();
        const string broken = "{ this is not json";
        File.WriteAllText(path, broken);
        var store = new ConfigStore(path);

        store.Load();
        Assert.True(store.IsCorrupt);

        Assert.Throws<ConfigUnreadableException>(() => store.Update(c => c with { SetupCompleted = true }));

        Assert.Equal(broken, File.ReadAllText(path));
        Assert.True(File.Exists(path + ".corrupt"));
    }

    /// <summary>
    /// A file with a value this build does not know (for example one written by a newer version)
    /// is unreadable too, and must be treated the same way instead of being overwritten.
    /// </summary>
    [Fact]
    public void UnknownEnumValueIsTreatedAsUnreadable()
    {
        var path = PathFor();
        const string newer = "{ \"RoleBindings\": [ { \"Role\": \"Teleport\", \"AdapterLuid\": 1 } ] }";
        File.WriteAllText(path, newer);
        var store = new ConfigStore(path);

        store.Load();
        Assert.True(store.IsCorrupt);
        Assert.Throws<ConfigUnreadableException>(() => store.Update(c => c with { SetupCompleted = true }));
        Assert.Equal(newer, File.ReadAllText(path));
    }

    /// <summary>Once the user repairs the file, the store reads it again and writes resume.</summary>
    [Fact]
    public void UpdateResumesAfterTheFileIsRepaired()
    {
        var path = PathFor();
        File.WriteAllText(path, "{ broken");
        var store = new ConfigStore(path);
        store.Load();
        Assert.Throws<ConfigUnreadableException>(() => store.Update(c => c with { SetupCompleted = true }));

        File.WriteAllText(path, "{}");

        var updated = store.Update(c => c with { SetupCompleted = true });
        Assert.True(updated.SetupCompleted);
        Assert.False(store.IsCorrupt);
        Assert.True(new ConfigStore(path).Load().SetupCompleted);
    }

    [Fact]
    public void UpdateCreatesAndPersistsWhenNoFileExists()
    {
        var path = PathFor();
        var store = new ConfigStore(path);

        store.Update(c => c with { SetupCompleted = true });

        Assert.True(new ConfigStore(path).Load().SetupCompleted);
        Assert.False(store.IsCorrupt);
    }

    /// <summary>
    /// Two writers changing the config at once must both land. Load-then-save from separate calls
    /// let one writer's change overwrite the other's.
    /// </summary>
    [Fact]
    public void ConcurrentUpdatesDoNotLoseChanges()
    {
        var path = PathFor();
        var store = new ConfigStore(path);
        const int writers = 50;

        Parallel.For(0, writers, i =>
        {
            var app = AppIdentity.ForExecutable($@"C:\Apps\app{i}.exe", $"App {i}");
            store.Update(c => c with { AppRules = c.AppRules.Append(AppRule.Create(app, RoleId.Gaming)).ToList() });
        });

        Assert.Equal(writers, new ConfigStore(path).Load().AppRules.Count);
    }
}
