using System.IO.Pipes;
using System.Text.Json;
using NetRoute.Core.Policy;

namespace NetRoute.Ipc;

public sealed class NamedPipeNetRouteClient(string? pipeName = null) : INetRouteClient, IDisposable
{
    private readonly string _pipeName = pipeName ?? IpcProtocol.PipeName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private IpcConnection? _connection;

    public Task PingAsync(CancellationToken ct = default) => CallAsync<object?>(IpcCommands.Ping, null, ct);
    public Task<ServiceStatusDto> GetStatusAsync(CancellationToken ct = default) => CallAsync<ServiceStatusDto>(IpcCommands.GetStatus, null, ct);
    public Task<IReadOnlyList<AdapterDto>> GetAdaptersAsync(CancellationToken ct = default) => CallAsync<IReadOnlyList<AdapterDto>>(IpcCommands.GetAdapters, null, ct);
    public Task<IReadOnlyList<ConnectionDto>> GetConnectionsAsync(CancellationToken ct = default) => CallAsync<IReadOnlyList<ConnectionDto>>(IpcCommands.GetConnections, null, ct);
    public Task CompleteSetupAsync(ulong gamingLuid, ulong downloadsLuid, CancellationToken ct = default) => CallAsync<object?>(IpcCommands.CompleteSetup, new CompleteSetupRequest(gamingLuid, downloadsLuid), ct);
    public Task<RoleChangeResultDto> SetRoleAdapterAsync(RoleId role, ulong adapterLuid, CancellationToken ct = default) => CallAsync<RoleChangeResultDto>(IpcCommands.SetRoleAdapter, new SetRoleAdapterRequest(role, adapterLuid), ct);
    public Task<AppStatusDto> AddRuleAsync(AppIdentity app, RoleId role, CancellationToken ct = default) => CallAsync<AppStatusDto>(IpcCommands.AddRule, new AddRuleRequest(app, role), ct);
    public Task UpdateRuleAsync(AppRule rule, CancellationToken ct = default) => CallAsync<object?>(IpcCommands.UpdateRule, new UpdateRuleRequest(rule), ct);
    public Task RemoveRuleAsync(Guid ruleId, CancellationToken ct = default) => CallAsync<object?>(IpcCommands.RemoveRule, new RuleIdRequest(ruleId), ct);
    public Task SetRulePausedAsync(Guid ruleId, bool paused, CancellationToken ct = default) => CallAsync<object?>(IpcCommands.SetRulePaused, new SetRulePausedRequest(ruleId, paused), ct);
    public Task SetEnforcementPausedAsync(bool paused, CancellationToken ct = default) => CallAsync<object?>(IpcCommands.SetEnforcementPaused, new SetPausedRequest(paused), ct);
    public Task EmergencyDisableAsync(CancellationToken ct = default) => CallAsync<object?>(IpcCommands.EmergencyDisable, null, ct);

    private async Task<T> CallAsync<T>(string command, object? payload, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await EnsureConnectedAsync(ct);
                    JsonElement? element = payload is null ? null : JsonSerializer.SerializeToElement(payload, IpcProtocol.JsonOptions);
                    await _connection!.WriteAsync(new IpcRequest { Command = command, Payload = element }, ct);
                    var response = await _connection.ReadAsync<IpcResponse>(ct) ?? throw new IOException("Service closed the pipe.");
                    if (!response.Ok) throw new NetRouteServiceException(response.Error ?? new IpcError { FriendlyMessage = "The service could not complete the request." });
                    if (typeof(T) == typeof(object)) return default!;
                    return response.Payload is { } responsePayload
                        ? responsePayload.Deserialize<T>(IpcProtocol.JsonOptions)!
                        : default!;
                }
                catch (NetRouteServiceException) { throw; }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    Disconnect();
                    if (attempt == 1) throw new ServiceUnavailableException("NetRoute service is not running.", ex);
                }
            }
            throw new ServiceUnavailableException("NetRoute service is not running.");
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_pipe?.IsConnected == true) return;
        Disconnect();
        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try { await _pipe.ConnectAsync(timeout.Token); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new TimeoutException("Pipe connection timed out.", ex); }
        _connection = new IpcConnection(_pipe);
    }

    private void Disconnect() { _connection?.Dispose(); _connection = null; _pipe = null; }
    public void Dispose() { Disconnect(); _gate.Dispose(); }
}
