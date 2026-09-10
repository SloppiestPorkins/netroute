using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetRoute.Ipc;

namespace NetRoute.Service;

public sealed class NamedPipeServer(NetRouteEngine engine, string? pipeName = null, bool secure = true, ILogger<NamedPipeServer>? logger = null)
{
    private readonly string _pipeName = pipeName ?? IpcProtocol.PipeName;

    public async Task RunAsync(CancellationToken ct)
    {
        var clients = new List<Task>();
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct);
                clients.RemoveAll(t => t.IsCompleted);
                clients.Add(ServeAsync(pipe, ct));
                pipe = null;   // ServeAsync owns it now
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
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
        await Task.WhenAll(clients);
    }

    private NamedPipeServerStream CreatePipe()
    {
        if (!secure)
            return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var security = new PipeSecurity();
        // SIDs are stable on localized Windows: interactive users may administer local
        // policy, while NETWORK is denied so the pipe cannot become a remote control path.
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
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var connection = new IpcConnection(pipe);
        while (pipe.IsConnected && !ct.IsCancellationRequested)
        {
            try
            {
                var request = await connection.ReadAsync<IpcRequest>(ct);
                if (request is null) return;
                await connection.WriteAsync(await DispatchAsync(request, ct), ct);
            }
            catch (IpcLineTooLongException)
            {
                await connection.WriteAsync(Failure("The request is too large.", "IPC line exceeded 1 MiB."), ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (JsonException ex) { await connection.WriteAsync(Failure("The request was not valid.", ex.ToString()), ct); }
            catch (Exception ex) { await connection.WriteAsync(Failure(ex), ct); }
        }
    }

    public async Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken ct = default)
    {
        try
        {
            object? value = request.Command switch
            {
                IpcCommands.Ping => null,
                IpcCommands.GetStatus => await engine.GetStatusAsync(ct),
                IpcCommands.GetAdapters => await engine.GetAdaptersAsync(ct),
                IpcCommands.GetConnections => await engine.GetConnectionsAsync(ct),
                IpcCommands.CompleteSetup => await CompleteSetup(request, ct),
                IpcCommands.SetRoleAdapter => await SetRole(request, ct),
                IpcCommands.AddRule => await AddRule(request, ct),
                IpcCommands.UpdateRule => await UpdateRule(request, ct),
                IpcCommands.RemoveRule => await RemoveRule(request, ct),
                IpcCommands.SetRulePaused => await SetRulePaused(request, ct),
                IpcCommands.SetEnforcementPaused => await SetPaused(request, ct),
                IpcCommands.EmergencyDisable => await EmergencyDisable(ct),
                _ => throw new NetRouteServiceException(new IpcError { FriendlyMessage = "NetRoute does not recognize that command.", TechnicalDetail = $"Unknown command: {request.Command}" })
            };
            return new IpcResponse { Ok = true, Payload = value is null ? null : JsonSerializer.SerializeToElement(value, IpcProtocol.JsonOptions) };
        }
        catch (Exception ex) { return Failure(ex); }
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
    private async Task<object?> SetPaused(IpcRequest r, CancellationToken ct) { await engine.SetEnforcementPausedAsync(Payload<SetPausedRequest>(r).Paused, ct); return null; }
    private async Task<object?> EmergencyDisable(CancellationToken ct) { await engine.EmergencyDisableAsync(ct); return null; }
    private static IpcResponse Failure(Exception ex) => ex is NetRouteServiceException service
        ? new() { Ok = false, Error = service.Error }
        : Failure("NetRoute could not complete that request.", ex.ToString());
    private static IpcResponse Failure(string friendly, string? technical) => new() { Ok = false, Error = new IpcError { FriendlyMessage = friendly, TechnicalDetail = technical } };
}
