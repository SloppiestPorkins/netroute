using System.Runtime.InteropServices;
using NetRoute.Windows.Wfp;
using Xunit;

namespace NetRoute.Tests.Windows;

/// <summary>
/// The decision that turns "the provider already exists" into either "carry on" or "another copy of
/// NetRoute is running", and the identities that let a proof run beside the service. The native
/// calls under it are not exercised here.
/// </summary>
public sealed class WfpSessionOwnershipTests
{
    private const uint WrongSession = WfpException.FWP_E_WRONG_SESSION;

    [Fact]
    public void An_unrefused_probe_means_the_objects_are_ours()
    {
        SessionClaim.Verify(() => 0, () => throw new InvalidOperationException("must not look"), _ => { });
    }

    [Fact]
    public void Wrong_session_with_another_NetRoute_process_visible_is_reported_at_once()
    {
        var waits = 0;
        var ex = Assert.Throws<AnotherInstanceException>(() => SessionClaim.Verify(
            () => WrongSession, () => [new OtherSession(4312, "NetRoute.Service")], _ => waits++));

        Assert.Equal(0, waits);
        Assert.Contains("Another copy of NetRoute is already running", ex.Message);
        Assert.Contains("NetRoute.Service, process 4312", ex.Message);
        Assert.Equal(["NetRoute.Service, process 4312"], ex.Owners);
    }

    [Fact]
    public void Our_own_previous_session_going_away_is_waited_out()
    {
        var probes = 0;
        var waits = 0;
        SessionClaim.Verify(() => ++probes < 4 ? WrongSession : 0, () => [], _ => waits++);

        Assert.Equal(4, probes);
        Assert.Equal(3, waits);
    }

    [Fact]
    public void Wrong_session_that_never_clears_and_shows_no_owner_still_fails_with_a_clear_message()
    {
        var ex = Assert.Throws<AnotherInstanceException>(() => SessionClaim.Verify(
            () => WrongSession, () => [], _ => { }, attempts: 3));

        Assert.Empty(ex.Owners);
        Assert.Contains("another program using", ex.Message);
        Assert.Contains("provider", ex.Message);
    }

    [Fact]
    public void A_session_list_that_cannot_be_read_is_treated_as_no_visible_owner()
    {
        var probes = 0;
        SessionClaim.Verify(() => ++probes < 2 ? WrongSession : 0, () => throw new WfpException("FwpmSessionEnum0", 5), _ => { });
        Assert.Equal(2, probes);
    }

    [Fact]
    public void Any_other_probe_failure_is_a_plain_engine_error_not_an_ownership_claim()
    {
        var ex = Assert.Throws<WfpException>(() => SessionClaim.Verify(
            () => WfpException.FWP_E_INVALID_PARAMETER, () => [], _ => { }));
        Assert.Equal(WfpException.FWP_E_INVALID_PARAMETER, ex.ErrorCode);
    }

    [Fact]
    public void Open_failure_text_names_the_cause()
    {
        Assert.Contains("administrator", WfpSession.DescribeOpenFailure(new WfpException("FwpmEngineOpen0", WfpException.ERROR_ACCESS_DENIED)));
        Assert.Contains("Base Filtering Engine", WfpSession.DescribeOpenFailure(new WfpException("FwpmEngineOpen0", WfpException.RPC_S_SERVER_UNAVAILABLE)));
        Assert.Contains("Another copy of NetRoute", WfpSession.DescribeOpenFailure(new AnotherInstanceException([])));
    }

    [Fact]
    public void The_default_identity_keeps_the_shipped_keys()
    {
        Assert.Equal(new Guid("6E9D1F2A-3B7C-4E58-9A21-D4C8B5E70A13"), WfpIdentity.Default.ProviderKey);
        Assert.Equal(new Guid("B41C7E90-52A8-4D3F-8C16-7F2E9A4B60D5"), WfpIdentity.Default.SubLayerKey);
        Assert.Equal(WfpIdentity.Default.ProviderKey, WfpSession.ProviderKey);
        Assert.Equal(WfpIdentity.Default.SubLayerKey, WfpSession.SubLayerKey);
        Assert.True(WfpIdentity.Default.IsDefault);
    }

    [Fact]
    public void An_instance_identity_is_stable_distinct_and_never_the_default()
    {
        var a = WfpIdentity.ForInstance("poc-wfp-measure");
        var again = WfpIdentity.ForInstance("  POC-wfp-measure ");
        var b = WfpIdentity.ForInstance("poc-wfp-or");

        Assert.Equal(a, again);
        Assert.NotEqual(a.ProviderKey, b.ProviderKey);
        Assert.NotEqual(a.SubLayerKey, b.SubLayerKey);
        Assert.NotEqual(a.ProviderKey, a.SubLayerKey);
        foreach (var id in new[] { a, b })
        {
            Assert.False(id.IsDefault);
            Assert.NotEqual(WfpIdentity.Default.ProviderKey, id.ProviderKey);
            Assert.NotEqual(WfpIdentity.Default.SubLayerKey, id.SubLayerKey);
            Assert.NotEqual(WfpIdentity.Default.SessionName, id.SessionName);
        }
        Assert.Throws<ArgumentException>(() => WfpIdentity.ForInstance(" "));
    }

    [Fact]
    public void Enumeration_template_matches_the_native_layout()
    {
        Assert.Equal(72, Marshal.SizeOf<WfpNative.FWPM_FILTER_ENUM_TEMPLATE0>());
        Assert.Equal(8, (int)Marshal.OffsetOf<WfpNative.FWPM_FILTER_ENUM_TEMPLATE0>("layerKey"));
        Assert.Equal(24, (int)Marshal.OffsetOf<WfpNative.FWPM_FILTER_ENUM_TEMPLATE0>("enumType"));
        Assert.Equal(32, (int)Marshal.OffsetOf<WfpNative.FWPM_FILTER_ENUM_TEMPLATE0>("providerContextTemplate"));
        Assert.Equal(40, (int)Marshal.OffsetOf<WfpNative.FWPM_FILTER_ENUM_TEMPLATE0>("numFilterConditions"));
        Assert.Equal(48, (int)Marshal.OffsetOf<WfpNative.FWPM_FILTER_ENUM_TEMPLATE0>("filterCondition"));
        Assert.Equal(56, (int)Marshal.OffsetOf<WfpNative.FWPM_FILTER_ENUM_TEMPLATE0>("actionMask"));
        Assert.Equal(64, (int)Marshal.OffsetOf<WfpNative.FWPM_FILTER_ENUM_TEMPLATE0>("calloutKey"));
    }
}
