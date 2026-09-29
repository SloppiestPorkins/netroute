using NetRoute.Core.Policy;

namespace NetRoute.Tests.Policy;

/// <summary>
/// Which of Windows' own downloaders are pinned to the Downloads network.
///
/// <para>The list is the whole feature: anything missing from it is a download that quietly
/// uses whichever line Windows fancies. Gaming Services was missing, and a Game Pass install
/// measured on real hardware put 1.27 GB down the Gaming line while the Xbox app itself sat
/// obediently on Downloads.</para>
/// </summary>
public class SystemDownloadsTests
{
    [Theory]
    [InlineData("DoSvc")]             // Delivery Optimization
    [InlineData("BITS")]
    [InlineData("wuauserv")]          // Windows Update
    [InlineData("InstallService")]    // Microsoft Store
    [InlineData("GamingServices")]    // Game Pass installs
    [InlineData("GamingServicesNet")] // the one that actually pulls the bytes
    public void EveryWindowsDownloaderIsCovered(string service)
        => Assert.Contains(service, SystemDownloadsPlan.WindowsDownloadServices);

    [Fact]
    public void ServiceNamesAreListedOnce()
        => Assert.Equal(SystemDownloadsPlan.WindowsDownloadServices.Count,
            SystemDownloadsPlan.WindowsDownloadServices.Distinct(StringComparer.OrdinalIgnoreCase).Count());
}
