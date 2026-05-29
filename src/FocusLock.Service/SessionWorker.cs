using System.IO.Pipes;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;
using FocusLock.Service.Ipc;

namespace FocusLock.Service;

/// <summary>
/// Named-pipe IPC server and screen-time enforcement loop.
/// Session state and handler logic live in <see cref="SessionManager"/>.
/// </summary>
public class SessionWorker : BackgroundService
{
    private readonly ILogger<SessionWorker> _log;
    private readonly SessionManager _manager;

    public SessionWorker(ILogger<SessionWorker> log, SessionManager manager)
    {
        _log     = log;
        _manager = manager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _manager.Initialize();

        var pipeTask       = RunPipeServerAsync(stoppingToken);
        var screenTimeTask = _manager.ScreenTime.RunAsync(stoppingToken);

        await Task.WhenAll(pipeTask, screenTimeTask);
    }

    // ── Named Pipe server ─────────────────────────────────────────────────────

    private async Task RunPipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = CreatePipeServer();
                await pipe.WaitForConnectionAsync(ct);
                _ = HandleClientAsync(pipe, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Pipe server error.");
                await Task.Delay(1000, ct);
            }
        }
    }

    private static NamedPipeServerStream CreatePipeServer()
    {
        return NamedPipeServerStreamAcl.Create(
            PipeConstants.PipeName,
            PipeDirection.InOut,
            PipeConstants.MaxConnections,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0, 0,
            PipeSecurityHelper.CreatePipeSecurity());
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                PipeMessage? msg;

                // 3-second read timeout prevents idle clients from exhausting pipe instances (DoS).
                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(PipeConstants.IpcReadTimeoutSeconds));
                    try
                    {
                        msg = await PipeFraming.ReadMessageAsync(pipe, timeoutCts.Token);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        _log.LogWarning("Client connection timed out while waiting for payload.");
                        return;
                    }
                }

                if (msg is null) return;

                if (PipeSecurityHelper.RequiresAdministrator(msg.Type))
                {
                    if (!PipeSecurityHelper.TryGetClientIsAdministrator(pipe, out var isAdmin) || !isAdmin)
                    {
                        await PipeFraming.WriteMessageAsync(pipe,
                            BuildReply(new AckResponse(false, "Administrator privileges required.")),
                            ct);
                        return;
                    }
                }

                PipeMessage response;
                try
                {
                    response = await DispatchAsync(msg, ct);
                }
                catch (OperationCanceledException)
                {
                    return; // service is stopping; close connection gracefully
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Dispatch error for message type {Type}.", msg.Type);
                    response = BuildReply(new AckResponse(false, "An internal service error occurred."));
                }

                await PipeFraming.WriteMessageAsync(pipe, response, ct);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Client handler error.");
            }
        }
    }

    private async Task<PipeMessage> DispatchAsync(PipeMessage msg, CancellationToken ct)
    {
        return msg.Type switch
        {
            PipeConstants.GetStatus          => BuildReply(_manager.GetStatus()),
            PipeConstants.GetSessionInfo     => BuildReply(_manager.GetSessionInfo()),
            PipeConstants.StartSession       => BuildReply(await _manager.StartSessionAsync(msg, ct)),
            PipeConstants.EndSession         => BuildReply(await _manager.EndSessionRequestAsync(msg, ct)),
            PipeConstants.IsBlocked          => BuildReply(_manager.CheckIsBlocked(msg)),
            PipeConstants.GetScreenTimeConfig  => BuildReply(_manager.ScreenTime.HandleGetConfig()),
            PipeConstants.SetScreenTimeConfig  => BuildReply(await _manager.HandleSetScreenTimeConfigAsync(msg)),
            PipeConstants.GetScreenTimeStatus  => BuildReply(_manager.ScreenTime.HandleGetStatus()),
            PipeConstants.ForceReset           => BuildReply(_manager.HandleForceReset()),
            PipeConstants.UnlockForSetup       => BuildReply(await _manager.HandleUnlockForSetupAsync()),
            _ => BuildReply(new AckResponse(false, $"Unknown message type: {msg.Type}"))
        };
    }

    private static PipeMessage BuildReply<T>(T payload)
        => PipeFraming.BuildRequest("Reply", payload);
}
