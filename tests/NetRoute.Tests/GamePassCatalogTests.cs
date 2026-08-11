using NetRoute.Windows.Apps;
using Xunit;

namespace NetRoute.Tests;

public class GamePassCatalogTests
{
    /// <summary>
    /// The Gaming Services repository records package *full* names, while packaged-app
    /// enumeration and WFP both work in *family* names. Getting this conversion wrong
    /// silently yields zero Game Pass matches — which is precisely how the first
    /// implementation failed, and it failed quietly rather than throwing.
    /// </summary>
    [Theory]
    [InlineData(
        "Microsoft.ProjectMeteoriteDigitalExtras_1.0.1.0_x64__8wekyb3d8bbwe",
        "Microsoft.ProjectMeteoriteDigitalExtras_8wekyb3d8bbwe")]
    [InlineData(
        "BethesdaSoftworks.TitanCampaignDLC_0.0.4.0_x64__3275kfvn8vcwc",
        "BethesdaSoftworks.TitanCampaignDLC_3275kfvn8vcwc")]
    [InlineData(
        "Xbox360BackwardCompatibil.PrimaryConkerLiveReloade_1.0.0.0_neutral__ksqcvrsvwz2jp",
        "Xbox360BackwardCompatibil.PrimaryConkerLiveReloade_ksqcvrsvwz2jp")]
    [InlineData(
        "IOInteractiveAS.1629748E3969B_1.2.3.0_x64__6h0y724g59e1w",
        "IOInteractiveAS.1629748E3969B_6h0y724g59e1w")]
    public void ConvertsPackageFullNameToFamilyName(string fullName, string expected)
    {
        Assert.Equal(expected, GamePassCatalog.ToFamilyName(fullName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("NoPublisherId")]
    [InlineData("Missing.DoubleUnderscore_1.0.0.0_x64")]
    public void RejectsMalformedFullNames(string fullName)
    {
        Assert.Null(GamePassCatalog.ToFamilyName(fullName));
    }

    /// <summary>Loading must not throw on a machine with no Gaming Services installed.</summary>
    [Fact]
    public void LoadsWithoutThrowing()
    {
        var catalog = GamePassCatalog.Load();
        Assert.True(catalog.Count >= 0);
    }
}
