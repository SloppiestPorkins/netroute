using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using NetRoute.Ipc;
using NetRoute.Service;
using static NetRoute.Tests.Service.NetRouteEngineTests;

namespace NetRoute.Tests.Ipc;

/// <summary>
/// Findings 3, 9, 19 and 20: a client that vanishes or misbehaves must not disturb the pipe server,
/// the framing must be bounded and cheap, and privileged commands and the update folder must be
/// protected.
/// </summary>
public sealed class PipeResilienceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"NetRoute.Resilience.{Guid.NewGuid():N}");

    private static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private NetRouteEngine Engine(string name)
        => new(new MutableAdapterSource(Ethernet(), Wifi()), new CountingBackend(), Path.Combine(_directory, name + ".json"));

    private static string NewPipe() => $"NetRoute.Test.{Guid.NewGuid():N}";

    // ------------------------------------------------------------ finding 3

    [Fact]
    public async Task A_client_that_disappears_mid_conversation_does_not_fault_the_server_or_its_shutdown()
    {
        using var engine = Engine("vanish");
        var name = NewPipe();
        using var stop = new CancellationTokenSource();
        var server = new NamedPipeServer(engine, name, secure: false).RunAsync(stop.Token);

        // Fire several pipelined requests and slam the pipe shut without reading a single answer, so
        // the server is writing to a pipe whose reader has gone.
        for (var i = 0; i < 30; i++)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2000);
            var burst = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"command\":\"getStatus\"}\n", 5)));
            await pipe.WriteAsync(burst);
            await pipe.FlushAsync();
            pipe.Dispose();
        }
        // Also leave one that connects and says nothing, and one that sends half a line then goes.
        var silent = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await silent.ConnectAsync(2000);
        var half = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await half.ConnectAsync(2000);
        await half.WriteAsync(Encoding.UTF8.GetBytes("{\"comm"));
        half.Dispose();

        // The server must still be serving everyone else.
        using var healthy = new NamedPipeNetRouteClient(name);
        await healthy.PingAsync();
        Assert.False(server.IsCompleted, "the server stopped serving");

        stop.Cancel();
        var finished = await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(server, finished);
        await server;   // a faulted server would rethrow here, as it did on the owner's PC
        silent.Dispose();
    }

    [Fact]
    public async Task Garbage_gets_an_error_and_the_same_connection_carries_on()
    {
        using var engine = Engine("garbage");
        var name = NewPipe();
        using var stop = new CancellationTokenSource();
        var server = new NamedPipeServer(engine, name, secure: false).RunAsync(stop.Token);
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000);
        using var connection = new IpcConnection(pipe);

        await pipe.WriteAsync(Encoding.UTF8.GetBytes("this is not json\n{\"command\":\"ping\"}\n"));
        var bad = await connection.ReadAsync<IpcResponse>();
        var good = await connection.ReadAsync<IpcResponse>();

        Assert.False(bad!.Ok);
        Assert.True(good!.Ok);
        stop.Cancel();
        await server;
    }

    [Fact]
    public async Task Stopping_with_clients_still_connected_ends_cleanly()
    {
        using var engine = Engine("stop");
        var name = NewPipe();
        using var stop = new CancellationTokenSource();
        var server = new NamedPipeServer(engine, name, secure: false).RunAsync(stop.Token);
        using var client = new NamedPipeNetRouteClient(name);
        await client.PingAsync();   // leaves a connected, idle client

        stop.Cancel();

        Assert.Same(server, await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(10))));
        await server;
    }

    // ------------------------------------------------------------ finding 9

    [Fact]
    public async Task A_large_answer_is_read_intact_even_when_the_pipe_delivers_it_a_byte_at_a_time()
    {
        var big = new IpcResponse { Ok = true, Payload = System.Text.Json.JsonSerializer.SerializeToElement(new string('a', 600_000)) };
        var buffer = new MemoryStream();
        await new IpcConnection(buffer).WriteAsync(big);
        buffer.Position = 0;

        var read = await new IpcConnection(new TrickleStream(buffer, 1)).ReadAsync<IpcResponse>();

        Assert.True(read!.Ok);
        Assert.Equal(600_002, read.Payload!.Value.GetRawText().Length);   // the string plus its two quotes
    }

    [Fact]
    public async Task An_oversized_line_is_drained_and_the_next_line_is_still_readable()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', IpcConnection.MaximumLineBytes + 50) + "\n{\"ok\":true}\n");
        var connection = new IpcConnection(new TrickleStream(new MemoryStream(bytes), 4096));

        await Assert.ThrowsAsync<IpcLineTooLongException>(() => connection.ReadAsync<IpcResponse>());
        var next = await connection.ReadAsync<IpcResponse>();

        Assert.True(next!.Ok);
    }

    [Fact]
    public async Task An_answer_over_the_cap_is_refused_before_anything_is_written()
    {
        var sink = new MemoryStream();
        var huge = new IpcResponse { Ok = true, Payload = System.Text.Json.JsonSerializer.SerializeToElement(new string('a', IpcConnection.MaximumLineBytes)) };

        await Assert.ThrowsAsync<IpcLineTooLongException>(() => new IpcConnection(sink).WriteAsync(huge));

        Assert.Equal(0, sink.Length);
    }

    [Fact]
    public void The_connection_list_is_bounded_at_the_source_and_keeps_the_interesting_rows()
    {
        var rows = Enumerable.Range(0, 6000).Select(i => new ConnectionDto
        {
            ProcessId = i,
            ProcessName = "SomeProgram",
            ExecutablePath = @"C:\Program Files\Some Vendor\Some Program\SomeProgram.exe",
            Protocol = NetRoute.Core.Traffic.TransportProtocol.Udp,
            LocalEndpoint = "192.168.1.20:5000",
            RemoteEndpoint = i % 3 == 0 ? null : "203.0.113.9:443",
            State = i % 3 == 1 ? "Established" : null
        }).ToList();

        var bounded = NamedPipeServer.Bound(rows);

        Assert.Equal(NamedPipeServer.MaxConnectionRows, bounded.Count);
        Assert.All(bounded, c => Assert.NotNull(c.RemoteEndpoint));
        Assert.Equal("Established", bounded[0].State);
        var size = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(bounded, IpcProtocol.JsonOptions).Length;
        Assert.True(size < IpcConnection.MaximumLineBytes / 2, $"{size} bytes is too close to the line cap");
    }

    // ------------------------------------------------------------ finding 19

    [Fact]
    public async Task Changing_the_update_feed_needs_an_administrator_but_reading_does_not()
    {
        using var engine = Engine("feed");
        var server = new NamedPipeServer(engine, "unused");
        var set = new IpcRequest
        {
            Command = IpcCommands.SetUpdateSettings,
            Payload = System.Text.Json.JsonSerializer.SerializeToElement(new SetUpdateSettingsRequest("https://example.com/feed.json", true), IpcProtocol.JsonOptions)
        };

        var refused = await server.DispatchAsync(set, default, ClientRights.Standard);
        var allowed = await server.DispatchAsync(set, default, ClientRights.Administrator);
        var read = await server.DispatchAsync(new IpcRequest { Command = IpcCommands.GetUpdateSettings }, default, ClientRights.Standard);

        Assert.False(refused.Ok);
        Assert.Contains("administrator", refused.Error!.FriendlyMessage);
        Assert.Null(engine.GetUpdateSettings().FeedUrl);   // the refused request changed nothing
        Assert.True(allowed.Ok);
        Assert.True(read.Ok);
    }

    [Fact]
    public async Task Over_the_real_secured_pipe_the_callers_own_token_decides()
    {
        using var engine = Engine("secured");
        var name = NewPipe();
        using var stop = new CancellationTokenSource();
        var server = new NamedPipeServer(engine, name, secure: true).RunAsync(stop.Token);
        using var client = new NamedPipeNetRouteClient(name);

        Task Set() => client.SetUpdateSettingsAsync("https://example.com/feed.json", false);

        if (IsAdmin())
        {
            await Set();
        }
        else
        {
            var refused = await Assert.ThrowsAsync<NetRouteServiceException>(Set);
            Assert.Contains("administrator", refused.Error.FriendlyMessage);
        }
        await client.GetUpdateSettingsAsync();   // reading is open to everyone
        stop.Cancel();
        await server;
    }

    // ------------------------------------------------------------ finding 20

    [Fact]
    public void Only_system_and_administrators_count_as_the_service()
    {
        Assert.True(NamedPipeNetRouteClient.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.True(NamedPipeNetRouteClient.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.False(NamedPipeNetRouteClient.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.WorldSid, null)));
        Assert.False(NamedPipeNetRouteClient.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
    }

    [Fact]
    public async Task A_pipe_squatted_by_an_ordinary_user_is_not_talked_to()
    {
        if (IsAdmin()) return;   // an elevated test process owns its pipes as Administrators, which is trusted
        var name = NewPipe();
        await using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepting = squatter.WaitForConnectionAsync();
        using var client = new NamedPipeNetRouteClient(name, verifyServer: true);

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.PingAsync());

        Assert.Contains("Something other than", error.Message);
        _ = accepting;
    }

    [Fact]
    public async Task The_server_will_not_join_a_pipe_name_somebody_else_already_holds()
    {
        using var engine = Engine("squat");
        var name = NewPipe();
        await using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var log = new RecordingLogger();
        using var stop = new CancellationTokenSource();
        var server = new NamedPipeServer(engine, name, secure: false, log).RunAsync(stop.Token);

        var deadline = Stopwatch.StartNew();
        while (!log.Errors.Any() && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);

        Assert.Single(log.Errors.Take(1));
        stop.Cancel();
        await server;
    }

    // ------------------------------------------------------------ finding 19, download folder

    private static string NewFolder(out string parent)
    {
        parent = Path.Combine(Path.GetTempPath(), $"NetRoute.Folder.{Guid.NewGuid():N}");
        return Path.Combine(parent, "updates");
    }

    [Fact]
    public void The_update_folder_ends_up_unwritable_by_ordinary_users_and_that_is_checked()
    {
        var folder = NewFolder(out var parent);
        try
        {
            var me = WindowsIdentity.GetCurrent().User!;
            UpdateFolderGuard.Prepare(folder, sid => sid == me || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid));

            var info = new DirectoryInfo(folder);
            var security = info.GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);

            // Hand Users write access, as a careless or hostile change would, and the check must object.
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
            Assert.Throws<UpdateFolderException>(() => UpdateFolderGuard.Verify(info, sid => sid == me));
        }
        finally { Directory.Delete(parent, true); }
    }

    [Fact]
    public void A_folder_owned_by_someone_untrusted_is_replaced_and_failure_to_secure_is_loud()
    {
        if (IsAdmin()) return;   // an elevated test process owns what it creates as Administrators, which is trusted
        var folder = NewFolder(out var parent);
        try
        {
            Directory.CreateDirectory(folder);
            var planted = Path.Combine(folder, "NetRoute-Setup-9.9.9.exe");
            File.WriteAllText(planted, "attacker");
            var me = WindowsIdentity.GetCurrent().User!;

            // The parent is accepted once; after that only SYSTEM/Administrators are trusted. The planted
            // folder is thrown away, and because its replacement is also owned by this user the guard
            // refuses rather than carries on.
            var calls = 0;
            Assert.Throws<UpdateFolderException>(() =>
                UpdateFolderGuard.Prepare(folder, sid => sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || (sid == me && calls++ == 0)));
            Assert.False(File.Exists(planted));
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
    }

    [Fact]
    public void A_junction_where_the_folder_should_be_is_removed_without_touching_its_target()
    {
        var folder = NewFolder(out var parent);
        var target = Path.Combine(Path.GetTempPath(), $"NetRoute.Target.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
            using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{folder}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!)
                mk.WaitForExit();
            Assert.True((new DirectoryInfo(folder).Attributes & FileAttributes.ReparsePoint) != 0, "could not create the junction");
            var me = WindowsIdentity.GetCurrent().User!;

            UpdateFolderGuard.Prepare(folder, sid => sid == me || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid));

            Assert.Equal(0, (int)(new DirectoryInfo(folder).Attributes & FileAttributes.ReparsePoint));
            Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    /// <summary>Hands out at most N bytes per read, like a pipe delivering in small pieces.</summary>
    private sealed class TrickleStream(Stream inner, int max) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, max));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer[..Math.Min(buffer.Length, max)], ct);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingLogger : ILogger<NamedPipeServer>
    {
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) lock (Errors) Errors.Add(formatter(state, exception));
        }
    }
}
