using System.IO.Pipes;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;

namespace FocusLock.UI.Services;

/// <summary>
/// Thin wrapper around the Named Pipe connection to FocusLockService.
/// Each call opens a fresh connection (fire-and-forget connection pooling is not needed at this scale).
/// </summary>
public class ServiceClient
{
    public async Task<T?> SendAsync<T>(string messageType, object? payload = null, CancellationToken ct = default)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeConstants.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);

            await pipe.ConnectAsync(PipeConstants.ConnectTimeoutMs, ct);
            var msg = PipeFraming.BuildRequest(messageType, payload);
            await PipeFraming.WriteMessageAsync(pipe, msg, ct);
            var reply = await PipeFraming.ReadMessageAsync(pipe, ct);
            return reply is null ? default : PipeFraming.ParsePayload<T>(reply);
        }
        catch
        {
            return default;
        }
    }

    public Task<StatusResponse?> GetStatusAsync(CancellationToken ct = default)
        => SendAsync<StatusResponse>(PipeConstants.GetStatus, ct: ct);

    public Task<SessionInfoResponse?> GetSessionInfoAsync(CancellationToken ct = default)
        => SendAsync<SessionInfoResponse>(PipeConstants.GetSessionInfo, ct: ct);

    public Task<AckResponse?> StartSessionAsync(StartSessionRequest request, CancellationToken ct = default)
        => SendAsync<AckResponse>(PipeConstants.StartSession, request, ct);

    public Task<AckResponse?> EndSessionAsync(string reason = "User request", CancellationToken ct = default)
        => SendAsync<AckResponse>(PipeConstants.EndSession, new EndSessionRequest(reason), ct);

    public Task<ScreenTimeConfigResponse?> GetScreenTimeConfigAsync(CancellationToken ct = default)
        => SendAsync<ScreenTimeConfigResponse>(PipeConstants.GetScreenTimeConfig, ct: ct);

    public Task<AckResponse?> SetScreenTimeConfigAsync(ScreenTimeConfig config, CancellationToken ct = default)
        => SendAsync<AckResponse>(PipeConstants.SetScreenTimeConfig, new SetScreenTimeConfigRequest(config), ct);

    public Task<ScreenTimeStatusResponse?> GetScreenTimeStatusAsync(CancellationToken ct = default)
        => SendAsync<ScreenTimeStatusResponse>(PipeConstants.GetScreenTimeStatus, ct: ct);
}
