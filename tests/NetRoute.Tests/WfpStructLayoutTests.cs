using System.Runtime.InteropServices;
using NetRoute.Windows.Wfp;
using Xunit;

namespace NetRoute.Tests;

/// <summary>
/// Asserts that the managed mirrors of the WFP structures match the x64 layout the
/// native API expects.
///
/// <para>This is the highest-consequence thing in the project to get wrong. A single
/// missing padding word does not fail loudly: it shifts every subsequent field, so
/// WFP either rejects the filter with an opaque parameter error or — much worse —
/// accepts a filter that matches something other than what was intended. Sizes are
/// the cheap check that catches essentially all of it, and offsets pin down the
/// fields where a union or an odd-sized member makes padding non-obvious.</para>
///
/// <para>Expected values are derived from fwptypes.h / fwpmtypes.h for x64.</para>
/// </summary>
public class WfpStructLayoutTests
{
    [Theory]
    [InlineData(typeof(WfpNative.FWPM_DISPLAY_DATA0), 16)]
    [InlineData(typeof(WfpNative.FWP_BYTE_BLOB), 16)]
    [InlineData(typeof(WfpNative.FWP_VALUE0), 16)]
    [InlineData(typeof(WfpNative.FWPM_FILTER_CONDITION0), 40)]
    [InlineData(typeof(WfpNative.FWPM_ACTION0), 20)]
    [InlineData(typeof(WfpNative.FWPM_SESSION0), 72)]
    [InlineData(typeof(WfpNative.FWPM_PROVIDER0), 64)]
    [InlineData(typeof(WfpNative.FWPM_SUBLAYER0), 72)]
    [InlineData(typeof(WfpNative.FWPM_FILTER0), 200)]
    public void StructSizeMatchesNativeLayout(Type type, int expectedSize)
    {
        Assert.Equal(expectedSize, Marshal.SizeOf(type));
    }

    [Theory]
    [InlineData("filterKey", 0)]
    [InlineData("displayData", 16)]
    [InlineData("flags", 32)]
    [InlineData("providerKey", 40)]
    [InlineData("providerData", 48)]
    [InlineData("layerKey", 64)]
    [InlineData("subLayerKey", 80)]
    [InlineData("weight", 96)]
    [InlineData("numFilterConditions", 112)]
    [InlineData("filterCondition", 120)]
    [InlineData("action", 128)]
    [InlineData("filterId", 176)]
    [InlineData("effectiveWeight", 184)]
    public void FilterFieldOffsetsMatchNativeLayout(string field, int expectedOffset)
    {
        var offset = Marshal.OffsetOf<WfpNative.FWPM_FILTER0>(field);
        Assert.Equal(expectedOffset, offset.ToInt32());
    }

    [Theory]
    [InlineData("fieldKey", 0)]
    [InlineData("matchType", 16)]
    [InlineData("conditionValue", 24)]
    public void ConditionFieldOffsetsMatchNativeLayout(string field, int expectedOffset)
    {
        var offset = Marshal.OffsetOf<WfpNative.FWPM_FILTER_CONDITION0>(field);
        Assert.Equal(expectedOffset, offset.ToInt32());
    }

    /// <summary>
    /// FWP_VALUE0's union is pointer-sized and must start on an 8-byte boundary,
    /// otherwise every by-pointer condition value (app ID blobs, interface LUIDs)
    /// is read from the wrong address.
    /// </summary>
    [Fact]
    public void ValueUnionIsEightByteAligned()
    {
        Assert.Equal(8, Marshal.OffsetOf<WfpNative.FWP_VALUE0>("value").ToInt32());
    }

    [Fact]
    public void ActionConstantsAreTerminating()
    {
        // A non-terminating permit or block would let filtering continue to lower-weight
        // filters, which would defeat the catch-all block that makes Strict mode strict.
        Assert.Equal(WfpNative.FWP_ACTION_FLAG_TERMINATING,
            WfpNative.FWP_ACTION_BLOCK & WfpNative.FWP_ACTION_FLAG_TERMINATING);
        Assert.Equal(WfpNative.FWP_ACTION_FLAG_TERMINATING,
            WfpNative.FWP_ACTION_PERMIT & WfpNative.FWP_ACTION_FLAG_TERMINATING);
        Assert.NotEqual(WfpNative.FWP_ACTION_BLOCK, WfpNative.FWP_ACTION_PERMIT);
    }
}
