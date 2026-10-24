using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetRoute.Ipc;

namespace NetRoute.Service;

/// <summary>What the caller on the other end of the pipe is allowed to do.</summary>
public enum ClientRights { Standard, Administrator }

public sealed class NamedPipeServer(NetRouteEngine engine, string? pipeName = null, bool secure = true, ILogger<NamedPipeServer>? logger = null)
{
    /// <summary>More simultaneous clients than this are turned away; the GUI and CLI need two; the headroom absorbs a burst whose predecessors are still being reaped.</summary>
    internal const int MaxClients = 64;
    /// <summary>A connection that sends nothing for this long is dropped; clients reconnect on demand.</summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    /// <summary>Bound on the live connection list at the source, so the line cap is not load-bearing.</summary>
    public const int MaxConnectionRows = 1500;

    private readonly string _pipeName = pipeName ?? IpcProtocol.PipeName;
    private int _active;

    public async Task RunAsync(CancellationToken ct)
    {
        var clients = new List<Task>();
        var first = true;
        var quickFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                try
                {
                    pipe = CreatePipe(first);
                    first = false;
                }
                catch (Exception ex) when (first && ex is IOException or UnauthorizedAccessException)
                {
                    // The name already exists and we did not make it: someone else holds it (or the old
                    // service is still dying). Never join an instance set we do not own.
                    logger?.LogError(ex, "The NetRoute pipe name is already in use by another process; will keep retrying.");
                    await Task.Delay(2000, ct);
                    continue;
                }
                await pipe.WaitForConnectionAsync(ct);
                quickFailures = 0;
                clients.RemoveAll(t => t.IsCompleted);
                if (Volatile.Read(ref _active) >= MaxClients)
                {
                    pipe.Dispose();
                    pipe = null;
                    continue;
                }
                Interlocked.Increment(ref _active);
                clients.Add(ServeAsync(pipe, ct));
                pipe = null;   // ServeAsync owns it now
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch (IOException ex) when (pipe is not null && quickFailures++ < 20)
            {
                // A client connected and vanished before we could talk to it. Normal; carry on at once.
                logger?.LogDebug(ex, "A pipe client left during connect.");
                pipe.Dispose();
            }
            catch (Exception ex)
            {
                // One failed accept must never take the endpoint down for good: that would
                // strand the GUI and CLI, Emergency Disable included, until the service restarts.
                pipe?.Dispose();
                logger?.LogWarning(ex, "Accepting a NetRoute pipe client failed; retrying.");
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
            }
        }
        try
        {
            // Cancelled clients finish quickly; do not let one that ignores the token hold the stop.
            await Task.WhenAny(Task.WhenAll(clients), Task.Delay(TimeSpan.FromSeconds(5)));
        }
        catch { /* ServeAsync never throws; belt and braces for shutdown */ }
    }

    private NamedPipeServerStream CreatePipe(bool firstInstance)
    {
        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        if (!secure)
            return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, options);

        var security = new PipeSecurity();
        // SIDs are stable on localized Windows: interactive users may use the pipe (commands that
        // matter for privilege are checked per caller), while NETWORK is denied so the pipe cannot
        // become a remote control path.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // The server's own identity needs CreateNewInstance to open the pipe's next instance.
        // As LocalSystem that is already covered; run any other way (a developer's console),
        // the second client would otherwise be refused. A no-op for the installed service.
        if (WindowsIdentity.GetCurrent().User is { } self)
            security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, options, 0, 0, security);
    }

    /// <summary>
    /// Serves one client. Never throws: a client that goes away, sends rubbish, or is slow is its own
    /// problem and must not fault the server, the accept loop, or shutdown.
    /// </summary>
    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var connection = new IpcConnection(pipe);
            var rights = secure ? CallerRights(pipe) : ClientRights.Administrator;
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                IpcRequest? request;
                try
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(IdleTimeout);
                    request = await connection.ReadAsync<IpcRequest>(idle.Token);
                }
                catch (IpcLineTooLongException)
                {
                    await TryWriteAsync(connection, Failure("The request is too large.", "IPC line exceeded 1 MiB."), ct);
                    return;
                }
                catch (JsonException ex)
                {
                    if (!await TryWriteAsync(connection, Failure("The request was not valid.", ex.Message), ct)) return;
                    continue;
                }
                // The idle timeout (not shutdown) lands in the outer OperationCanceledException and drops the client.
                if (request is null) return;
                IpcResponse response;
                try { response = await DispatchAsync(request, ct, rights); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { response = Failure(ex); }
                if (!await TryWriteAsync(connection, response, ct)) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException or InvalidOperationException)
        {
            logger?.LogDebug(ex, "A pipe client went away.");   // routine: an app being closed or replaced
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Serving a pipe client failed; that client was dropped.");
        }
        finally
        {
            try { pipe.Dispose(); } catch { }
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary>
    /// Writes a response; false means the client is gone and the connection should end. A client
    /// leaving mid-response is not an error.
    /// </summary>
    private async Task<bool> TryWriteAsync(IpcConnection connection, IpcResponse response, CancellationToken ct)
    {
        try
        {
            await connection.WriteAsync(response, ct);
            return true;
        }
        catch (IpcLineTooLongException)
        {
            // Nothing was written, so the stream is intact: say so instead of leaving the client waiting.
            try
            {
                await connection.WriteAsync(Failure("That answer is too large to send.", "IPC response exceeded 1 MiB."), ct);
                return true;
            }
            catch (Exception) { return false; }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            logger?.LogDebug(ex, "A pipe client left before its answer was sent.");
            return false;
        }
    }

    /// <summary>
    /// Is the caller an elevated administrator (or SYSTEM)? A UAC-filtered token has Administrators as
    /// deny-only, which correctly counts as standard. Fails closed.
    /// </summary>
    private static ClientRights CallerRights(NamedPipeServerStream pipe)
    {
        try
        {
            var admin = false;
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            });
            return admin ? ClientRights.Administrator : ClientRights.Standard;
        }
        catch (Exception) { return ClientRights.Standard; }
    }

    /// <summary>
    /// Runs one request. <paramref name="caller"/> defaults to Administrator because in-process
    /// callers are trusted; the pipe always passes the real caller's rights.
    /// </summary>
    public async Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken ct = default, ClientRights caller = ClientRights.Administrator)
    {
        try
        {
            if (request.Command == IpcCommands.SetUpdateSettings && caller != ClientRights.Administrator)
                throw new NetRouteServiceException(new IpcError
                {
                    FriendlyMessage = "Changing the update address needs administrator rights. Run the command from an elevated prompt.",
                    TechnicalDetail = "setUpdateSettings refused: caller is not an elevated administrator."
                });
            object? value = request.Command switch
            {
                IpcCommands.Ping => null,
                IpcCommands.GetStatus => await engine.GetStatusAsync(ct),
                IpcCommands.GetAdapters => await engine.GetAdaptersAsync(ct),
                IpcCommands.GetConnections => Bound(await engine.GetConnectionsAsync(ct)),
                IpcCommands.CompleteSetup => await CompleteSetup(request, ct),
                IpcCommands.SetRoleAdapter => await SetRole(request, ct),
                IpcCommands.AddRule => await AddRule(request, ct),
                IpcCommands.UpdateRule => await UpdateRule(request, ct),
                IpcCommands.RemoveRule => await RemoveRule(request, ct),
                IpcCommands.SetRulePaused => await SetRulePaused(request, ct),
                IpcCommands.SetEnforcementPaused => await SetPaused(request, ct),
                IpcCommands.EmergencyDisable => await EmergencyDisable(ct),
                IpcCommands.FixRouteTie => await engine.FixRouteTieAsync(ct),
                IpcCommands.GetAppRates => engine.GetAppRates(),
                IpcCommands.SetSystemDownloads => await SetSystemDownloads(request, ct),
                IpcCommands.SetPauseDownloads => await SetPauseDownloads(request, ct),
                IpcCommands.SetQuietHours => await SetQuietHours(request, ct),
                IpcCommands.StartSelfTest => await engine.StartSelfTestAsync(ct),
                IpcCommands.GetSelfTest => engine.GetSelfTest(),
                IpcCommands.GetUsageHistory => engine.GetUsageHistory(Payload<UsageHistoryRequest>(request).Days),
                IpcCommands.GetConnectionHistory => engine.GetConnectionHistory(Payload<UsageHistoryRequest>(request).Days),
                IpcCommands.CheckForUpdate => await engine.CheckForUpdateAsync(ct),
                IpcCommands.DownloadUpdate => await engine.DownloadUpdateAsync(ct),
                IpcCommands.GetUpdateSettings => engine.GetUpdateSettings(),
                IpcCommands.SetUpdateSettings => await SetUpdateSettings(request, ct),
                _ => throw new NetRouteServiceException(new IpcError { FriendlyMessage = "NetRoute does not recognize that command.", TechnicalDetail = $"Unknown command: {request.Command}" })
            };
            return new IpcResponse { Ok = true, Payload = value is null ? null : JsonSerializer.SerializeToElement(value, IpcProtocol.JsonOptions) };
        }
        catch (Exception ex) { return Failure(ex); }
    }

    /// <summary>
    /// Caps the live list at the source. Connections with a remote address come first (those are what
    /// people look at), established before the rest; listeners and idle UDP sockets go last.
    /// </summary>
    public static IReadOnlyList<ConnectionDto> Bound(IReadOnlyList<ConnectionDto> all)
    {
        if (all.Count <= MaxConnectionRows) return all;
        return all.Select((c, i) => (c, i))
            .OrderBy(x => x.c.RemoteEndpoint is null ? 2 : x.c.State == "Established" ? 0 : 1)
            .ThenBy(x => x.i)
            .Take(MaxConnectionRows)
            // Carried on every row that survives, so a list that has been cut cannot be shown as
            // though it were complete. The rows dropped are the idle and listening ones.
            .Select(x => x.c with { TotalBeforeCut = all.Count })
            .ToList();
    }

    private static T Payload<T>(IpcRequest request) => request.Payload is { } payload
        ? payload.Deserialize<T>(IpcProtocol.JsonOptions)!
        : throw new NetRouteServiceException(new IpcError { FriendlyMessage = "This request is missing required information." });
    private async Task<object?> CompleteSetup(IpcRequest r, CancellationToken ct) { var p = Payload<CompleteSetupRequest>(r); await engine.CompleteSetupAsync(p.GamingLuid, p.DownloadsLuid, ct); return null; }
    private async Task<object?> SetRole(IpcRequest r, CancellationToken ct) { var p = Payload<SetRoleAdapterRequest>(r); return await engine.SetRoleAdapterAsync(p.Role, p.AdapterLuid, ct); }
    private async Task<object?> AddRule(IpcRequest r, CancellationToken ct) { var p = Payload<AddRuleRequest>(r); return await engine.AddRuleAsync(p.App, p.Role, ct); }
    private async Task<object?> UpdateRule(IpcRequest r, CancellationToken ct) { await engine.UpdateRuleAsync(Payload<UpdateRuleRequest>(r).Rule, ct); return null; }
    private async Task<object?> RemoveRule(IpcRequest r, CancellationToken ct) { await engine.RemoveRuleAsync(Payload<RuleIdRequest>(r).RuleId, ct); return null; }
    private async Task<object?> SetRulePaused(IpcRequest r, CancellationToken ct) { var p = Payload<SetRulePausedRequest>(r); await engine.SetRulePausedAsync(p.RuleId, p.Paused, ct); return null; }
    private async Task<object?> SetPaused(IpcRequest r, CancellationToken ct) { var p = Payload<SetPausedRequest>(r); await engine.SetEnforcementPausedAsync(p.Paused, p.Minutes, ct); return null; }
    private async Task<object?> SetSystemDownloads(IpcRequest r, CancellationToken ct) { await engine.SetSystemDownloadsAsync(Payload<SetSystemDownloadsRequest>(r).Enabled, ct); return null; }
    private async Task<object?> SetPauseDownloads(IpcRequest r, CancellationToken ct) { await engine.SetPauseDownloadsAsync(Payload<SetPauseDownloadsRequest>(r).Enabled, ct); return null; }
    private async Task<object?> SetQuietHours(IpcRequest r, CancellationToken ct) { var p = Payload<SetQuietHoursRequest>(r); await engine.SetQuietHoursAsync(p.FromHour, p.ToHour, ct); return null; }
    private async Task<object?> SetUpdateSettings(IpcRequest r, CancellationToken ct) { var p = Payload<SetUpdateSettingsRequest>(r); await engine.SetUpdateSettingsAsync(p.FeedUrl, p.Automatic, ct); return null; }
    private async Task<object?> EmergencyDisable(CancellationToken ct) { await engine.EmergencyDisableAsync(ct); return null; }
    private static IpcResponse Failure(Exception ex) => ex is NetRouteServiceException service
        ? new() { Ok = false, Error = service.Error }
        : Failure("NetRoute could not complete that request.", ex.ToString());
    private static IpcResponse Failure(string friendly, string? technical) => new() { Ok = false, Error = new IpcError { FriendlyMessage = friendly, TechnicalDetail = technical } };
}
