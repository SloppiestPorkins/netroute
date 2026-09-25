using NetRoute.Core.Policy;
using NetRoute.Windows.Split;

namespace NetRoute.Tests.Policy;

public sealed class FolderRuleTests : IDisposable
{
    private readonly string _library = Path.Combine(Path.GetTempPath(), $"NetRoute.Library.{Guid.NewGuid():N}");

    public FolderRuleTests()
    {
        Directory.CreateDirectory(Path.Combine(_library, @"steamapps\common\Halo"));
        Directory.CreateDirectory(Path.Combine(_library, @"steamapps\common\DayZ\bin"));
        foreach (var file in new[] { @"steamapps\common\Halo\halo.exe", @"steamapps\common\DayZ\bin\dayz.exe" })
        {
            File.WriteAllText(Path.Combine(_library, file), "");
        }
    }

    [Fact]
    public void AFolderRuleCoversEveryProgramInside()
    {
        var rule = AppIdentity.ForFolder(_library, "Steam library");
        var halo = Path.Combine(_library, @"steamapps\common\Halo\halo.exe");

        // A launcher's rule would leave steamapps out; a folder rule is the user asking for all of it.
        Assert.True(AppMatching.Covers(rule, halo, null));
        Assert.False(AppMatching.Covers(rule, @"C:\Elsewhere\other.exe", null));
        Assert.Contains(SplitImagePaths.For(rule), p => p.EndsWith("halo.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(SplitImagePaths.For(rule), p => p.EndsWith("dayz.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AFolderRuleIsIdentifiedByItsFolder()
    {
        Assert.Equal(AppIdentityKind.Folder, AppIdentity.ForFolder(_library).Kind);
        Assert.StartsWith("dir:", AppIdentity.ForFolder(_library).StableKey);
        // Trailing slash or not, it is the same rule.
        Assert.Equal(AppIdentity.ForFolder(_library).StableKey, AppIdentity.ForFolder(_library + "\\").StableKey);
        Assert.Equal(Path.GetFileName(_library), AppIdentity.ForFolder(_library).DisplayName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_library)) Directory.Delete(_library, true);
    }
}
