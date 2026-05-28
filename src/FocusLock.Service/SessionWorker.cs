using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;

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
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeConstants.PipeName,
            PipeDirection.InOut,
            PipeConstants.MaxConnections,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0, 0,
            security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                var msg = await PipeFraming.ReadMessageAsync(pipe, ct);
                if (msg is null) return;

                var response = await DispatchAsync(msg, ct);
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
            PipeConstants.EndSession         => BuildReply(_manager.EndSessionRequest(msg)),
            PipeConstants.IsBlocked          => BuildReply(_manager.CheckIsBlocked(msg)),
            PipeConstants.GetScreenTimeConfig  => BuildReply(_manager.ScreenTime.HandleGetConfig()),
            PipeConstants.SetScreenTimeConfig  => BuildReply(await _manager.HandleSetScreenTimeConfigAsync(msg)),
            PipeConstants.GetScreenTimeStatus  => BuildReply(_manager.ScreenTime.HandleGetStatus()),
            _ => BuildReply(new AckResponse(false, $"Unknown message type: {msg.Type}"))
        };
    }

    private static PipeMessage BuildReply<T>(T payload)
        => PipeFraming.BuildRequest("Reply", payload);
}
