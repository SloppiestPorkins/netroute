using System.IO.Pipes;
using System.Text;
using NetRoute.Core.Policy;
using NetRoute.Ipc;
using NetRoute.Service;
using static NetRoute.Tests.Service.NetRouteEngineTests;

namespace NetRoute.Tests.Ipc;

public sealed class NamedPipeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"NetRoute.PipeTests.{Guid.NewGuid():N}");

    [Fact]
    public async Task ClientRoundTripsStatusAndMutations()
    {
        var pipeName = $"NetRoute.Test.{Guid.NewGuid():N}";
        using var engine = new NetRouteEngine(new MutableAdapterSource(Ethernet(), Wifi()), new CountingBackend(), Path.Combine(_directory, "config.json"));
        using var stop = new CancellationTokenSource();
        var serverTask = new NamedPipeServer(engine, pipeName, secure: false).RunAsync(stop.Token);
        using var client = new NamedPipeNetRouteClient(pipeName);

        await client.CompleteSetupAsync(Ethernet().Luid, Wifi().Luid);
        var added = await client.AddRuleAsync(AppIdentity.ForExecutable(@"C:\Games\Halo.exe", "Halo Infinite"), RoleId.Gaming);
        Assert.Equal("Halo Infinite", added.Rule.App.DisplayName);
        Assert.True((await client.GetStatusAsync()).SetupCompleted);
        await client.SetRulePausedAsync(added.Rule.Id, true);
        Assert.True((await client.GetStatusAsync()).Apps.Single().Rule.Paused);
        stop.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task UnknownCommandReturnsFriendlyError()
    {
        using var engine = new NetRouteEngine(new MutableAdapterSource(Ethernet()), new CountingBackend(), Path.Combine(_directory, "unknown.json"));
        var response = await new NamedPipeServer(engine, "unused").DispatchAsync(new IpcRequest { Command = "futureCommand" });
        Assert.False(response.Ok);
        Assert.Equal("NetRoute does not recognize that command.", response.Error!.FriendlyMessage);
    }

    [Fact]
    public async Task OversizedLineGetsErrorThenConnectionCloses()
    {
        var pipeName = $"NetRoute.Test.{Guid.NewGuid():N}";
        using var engine = new NetRouteEngine(new MutableAdapterSource(Ethernet()), new CountingBackend(), Path.Combine(_directory, "large.json"));
        using var stop = new CancellationTokenSource();
        var serverTask = new NamedPipeServer(engine, pipeName, secure: false).RunAsync(stop.Token);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(1000);
        var large = Encoding.UTF8.GetBytes(new string('x', IpcConnection.MaximumLineBytes + 1) + "\n");
        await pipe.WriteAsync(large);
        using var connection = new IpcConnection(pipe);
        var response = await connection.ReadAsync<IpcResponse>();
        Assert.False(response!.Ok);
        Assert.Contains("too large", response.Error!.FriendlyMessage);
        stop.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task MissingServerThrowsServiceUnavailable()
    {
        using var client = new NamedPipeNetRouteClient($"NetRoute.Test.{Guid.NewGuid():N}");
        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.PingAsync());
        Assert.Equal("NetRoute service is not running.", error.Message);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
