using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;
using FocusLock.Core.Storage;
using FocusLock.Service.Blocking;

namespace FocusLock.Service;

public class SessionWorker : BackgroundService
{
    private readonly ILogger<SessionWorker> _log;
    private FocusSession? _session;
    private readonly AppBlocker _appBlocker;
    private readonly WebsiteBlocker _websiteBlocker;
    private readonly StrictModeManager _strictMode;
    private readonly BlockPageServer _blockPageServer;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    public SessionWorker(ILogger<SessionWorker> log)
    {
        _log = log;
        _appBlocker = new AppBlocker(log);
        _websiteBlocker = new WebsiteBlocker(log);
        _strictMode = new StrictModeManager(log);
        _blockPageServer = new BlockPageServer(log, new SessionNotifier(log));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EnsureDataDirectory();
        RecoverSession();

        var pipeTask = RunPipeServerAsync(stoppingToken);
        var monitorTask = RunMonitorLoopAsync(stoppingToken);
        var deadlineTask = RunDeadlineWatcherAsync(stoppingToken);

        await Task.WhenAll(pipeTask, monitorTask, deadlineTask);
    }

    // ── Initialization ────────────────────────────────────────────────────────

    private void EnsureDataDirectory()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "FocusLock");
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to create data directory.");
        }
    }

    // ── Session recovery ──────────────────────────────────────────────────────

    private void RecoverSession()
    {
        var saved = SessionRepository.Load();
        if (saved is null || saved.Status != SessionStatus.Active) return;

        if (DateTime.UtcNow >= saved.DeadlineUtc)
        {
            _log.LogInformation("Recovered expired session {Id}; running cleanup.", saved.Id);
            EndSession(saved);
            return;
        }

        _log.LogInformation("Resuming active session {Id} (expires {Deadline:u}).", saved.Id, saved.DeadlineUtc);
        _session = saved;
        _appBlocker.Apply(_session);
        _websiteBlocker.Apply(_session);
        _blockPageServer.Start(_session);

        if (_session.Mode == SessionMode.Strict)
        {
            // Re-apply all strict protections. These are idempotent — re-adding a deny
            // ACE that already exists is harmless, and UnlockIfeoAcls removes all of them.
            _strictMode.Activate(_session);
            _appBlocker.LockIfeoAcls(_session);
            _websiteBlocker.LockHostsAcl(_session);
        }
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
            PipeConstants.GetStatus => BuildReply(GetStatus()),
            PipeConstants.GetSessionInfo => BuildReply(GetSessionInfo()),
            PipeConstants.StartSession => BuildReply(await StartSessionAsync(msg, ct)),
            PipeConstants.EndSession => BuildReply(EndSessionRequest(msg)),
            PipeConstants.IsBlocked => BuildReply(CheckIsBlocked(msg)),
            _ => BuildReply(new AckResponse(false, $"Unknown message type: {msg.Type}"))
        };
    }

    private static PipeMessage BuildReply<T>(T payload)
        => PipeFraming.BuildRequest("Reply", payload);

    // ── Handlers ──────────────────────────────────────────────────────────────

    private StatusResponse GetStatus()
    {
        var s = _session;
        return new StatusResponse(
            s?.Status ?? SessionStatus.Idle,
            s?.Mode ?? SessionMode.None,
            s?.DeadlineUtc);
    }

    private SessionInfoResponse GetSessionInfo()
    {
        var s = _session;
        if (s is null)
            return new SessionInfoResponse(SessionStatus.Idle, SessionMode.None, null, null, null, null, null);

        return new SessionInfoResponse(
            s.Status, s.Mode, s.StartedAtUtc, s.DeadlineUtc,
            s.BlockedApps, s.BlockedSites,
            s.DeadlineUtc - DateTime.UtcNow);
    }

    private async Task<AckResponse> StartSessionAsync(PipeMessage msg, CancellationToken ct)
    {
        var req = PipeFraming.ParsePayload<StartSessionRequest>(msg);
        if (req is null) return new AckResponse(false, "Invalid request payload.");
        if (req.DeadlineUtc <= DateTime.UtcNow.AddMinutes(1))
            return new AckResponse(false, "Deadline must be at least 1 minute in the future.");
        if (req.Mode == SessionMode.Strict && !req.ConsentGiven)
            return new AckResponse(false, "Consent required for Strict mode.");

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_session?.Status == SessionStatus.Active)
                return new AckResponse(false, "A session is already active.");

            var session = new FocusSession
            {
                Id = Guid.NewGuid(),
                Mode = req.Mode,
                Status = SessionStatus.Active,
                StartedAtUtc = DateTime.UtcNow,
                DeadlineUtc = req.DeadlineUtc,
                BlockedApps = req.Apps,
                BlockedSites = req.Sites
            };

            if (req.Mode == SessionMode.Strict)
            {
                var strictResult = _strictMode.Activate(session);
                if (!strictResult.Success) return strictResult;
            }

            _appBlocker.Apply(session);
            _websiteBlocker.Apply(session);
            _blockPageServer.Start(session);

            if (req.Mode == SessionMode.Strict)
            {
                _appBlocker.LockIfeoAcls(session);
                _websiteBlocker.LockHostsAcl(session);
            }

            SessionRepository.Save(session);
            _session = session;

            _log.LogInformation("Session {Id} started ({Mode}, expires {Deadline:u}).",
                session.Id, session.Mode, session.DeadlineUtc);
            return new AckResponse(true);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private AckResponse EndSessionRequest(PipeMessage msg)
    {
        var s = _session;
        if (s is null) return new AckResponse(false, "No active session.");
        if (s.Mode == SessionMode.Strict)
            return new AckResponse(false, "Strict mode sessions cannot be ended early.");

        EndSession(s);
        return new AckResponse(true);
    }

    private IsBlockedResponse CheckIsBlocked(PipeMessage msg)
    {
        var req = PipeFraming.ParsePayload<IsBlockedRequest>(msg);
        if (req is null || _session is null || _session.Status != SessionStatus.Active)
            return new IsBlockedResponse(false, null, null);

        var match = _session.BlockedApps.FirstOrDefault(a =>
            string.Equals(a.ExeName, req.ExeName, StringComparison.OrdinalIgnoreCase));

        return match is not null
            ? new IsBlockedResponse(true, _session.DeadlineUtc, match.DisplayName)
            : new IsBlockedResponse(false, null, null);
    }

    // ── Session lifecycle ─────────────────────────────────────────────────────

    private void EndSession(FocusSession session)
    {
        _log.LogInformation("Ending session {Id}.", session.Id);

        // For strict sessions: unlock ACLs before modifying the protected resources,
        // then restore the service DACL last so the service can be managed normally again.
        if (session.Mode == SessionMode.Strict)
        {
            _websiteBlocker.UnlockHostsAcl(session);
            _appBlocker.UnlockIfeoAcls(session);
        }

        _appBlocker.Remove(session);
        _websiteBlocker.Remove(session);
        _blockPageServer.Stop();

        if (session.Mode == SessionMode.Strict)
            _strictMode.Deactivate(session);

        session.Status = SessionStatus.Idle;
        SessionRepository.Save(null);
        _session = null;
    }

    // ── Background loops ──────────────────────────────────────────────────────

    private async Task RunMonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var s = _session;
                if (s?.Status == SessionStatus.Active)
                {
                    _appBlocker.KillRunningBlockedApps(s);
                    _appBlocker.VerifyAndRepairIfeo(s);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Monitor loop error.");
            }
            await Task.Delay(2000, ct);
        }
    }

    private async Task RunDeadlineWatcherAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var s = _session;
                if (s?.Status == SessionStatus.Active && DateTime.UtcNow >= s.DeadlineUtc)
                {
                    _log.LogInformation("Session {Id} deadline reached.", s.Id);
                    await _sessionLock.WaitAsync(ct);
                    try { EndSession(s); }
                    finally { _sessionLock.Release(); }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Deadline watcher error.");
            }
            await Task.Delay(5000, ct);
        }
    }
}
