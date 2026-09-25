using NetRoute.Ipc;
using NetRoute.Service;

namespace NetRoute.Tests.Service;

/// <summary>
/// The rules the update feed has to obey. These matter more than most parsing tests: what comes
/// back decides whether NetRoute downloads an executable and offers to run it as administrator.
/// </summary>
public class UpdaterTests
{
    private static readonly Version Current = new(1, 1, 0);

    [Fact]
    public void A_newer_version_is_an_update()
    {
        var found = Updater.Read("""
            { "version": "1.2.0", "url": "https://example.com/NetRoute-Setup-1.2.0.exe",
              "sha256": "ABC", "notes": "Quiet hours", "size": 1234 }
            """, Current);

        Assert.NotNull(found);
        Assert.Equal("1.2.0", found!.Version);
        Assert.Equal(UpdateState.Available, found.State);
        Assert.Equal("ABC", found.Sha256);
        Assert.Equal(1234, found.SizeBytes);
        Assert.Equal("Quiet hours", found.Notes);
    }

    [Theory]
    [InlineData("1.1.0")]
    [InlineData("1.0.9")]
    [InlineData("0.4.0")]
    public void The_same_or_an_older_version_is_not(string version)
        => Assert.Null(Updater.Read($$"""{ "version": "{{version}}", "url": "https://example.com/x.exe" }""", Current));

    [Theory]
    [InlineData("""{ "version": "1.2.0" }""")]
    [InlineData("""{ "url": "https://example.com/x.exe" }""")]
    [InlineData("""{ "version": "next week", "url": "https://example.com/x.exe" }""")]
    [InlineData("""{ "version": "1.2.0", "url": "" }""")]
    [InlineData("[]")]
    [InlineData("not json at all")]
    public void A_feed_that_says_nothing_useful_is_a_failure_rather_than_an_update(string json)
    {
        var found = Updater.Read(json, Current);

        Assert.NotNull(found);
        Assert.Equal(UpdateState.Failed, found!.State);
        Assert.NotNull(found.Problem);
    }

    [Fact]
    public void A_feed_with_no_hash_still_reports_the_version_so_the_user_gets_a_link()
    {
        // NetRoute will not download this one, because nothing could prove what arrived.
        var found = Updater.Read("""{ "version": "2.0.0", "url": "https://example.com/x.exe" }""", Current);

        Assert.NotNull(found);
        Assert.Equal(UpdateState.Available, found!.State);
        Assert.Null(found.Sha256);
    }

    [Fact]
    public void A_feed_saved_with_a_byte_order_mark_still_reads()
    {
        var found = Updater.Read("﻿" + """{ "version": "1.2.0", "url": "https://example.com/x.exe" }""", Current);

        Assert.Equal("1.2.0", found?.Version);
    }
}
