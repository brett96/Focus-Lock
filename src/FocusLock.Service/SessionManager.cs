using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;
using FocusLock.Core.Storage;
using FocusLock.Service.Blocking;

namespace FocusLock.Service;

/// <summary>
/// Singleton that owns all session state and exposes handler methods to the
/// BackgroundService workers that serve the named-pipe and enforcement loops.
/// </summary>
public class SessionManager
{
    private readonly ILogger _log;
    private FocusSession? _session;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly AppBlocker _appBlocker;
    private readonly WebsiteBlocker _websiteBlocker;
    private readonly StrictModeManager _strictMode;
    private readonly BlockPageServer _blockPageServer;
    private readonly ScreenTimeManager _screenTime;
    private bool _initialized;
    private readonly object _initLock = new();

    public FocusSession?     Session     => _session;
    public AppBlocker        AppBlocker  => _appBlocker;
    public ScreenTimeManager ScreenTime  => _screenTime;
    public SemaphoreSlim     SessionLock => _sessionLock;

    public SessionManager(ILogger<SessionManager> log)
    {
        _log = log;
        _appBlocker      = new AppBlocker(log);
        _websiteBlocker  = new WebsiteBlocker(log);
        _strictMode      = new StrictModeManager(log);
        _blockPageServer = new BlockPageServer(log, new SessionNotifier(log));
        _screenTime      = new ScreenTimeManager(log, _appBlocker, () => _session);
    }

    // ── Initialization ────────────────────────────────────────────────────────

    public void Initialize()
    {
        lock (_initLock)
        {
            if (_initialized) return;
            _initialized = true;
        }
        EnsureDataDirectory();
        RecoverSession();
    }

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
            _strictMode.Activate(_session);
            _appBlocker.LockIfeoAcls(_session);
            _websiteBlocker.LockHostsAcl(_session);
        }

        _screenTime.OnSessionStarted();
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    public StatusResponse GetStatus()
    {
        var s = _session;
        return new StatusResponse(
            s?.Status ?? SessionStatus.Idle,
            s?.Mode ?? SessionMode.None,
            s?.DeadlineUtc);
    }

    public SessionInfoResponse GetSessionInfo()
    {
        var s = _session;
        if (s is null)
            return new SessionInfoResponse(SessionStatus.Idle, SessionMode.None, null, null, null, null, null);

        return new SessionInfoResponse(
            s.Status, s.Mode, s.StartedAtUtc, s.DeadlineUtc,
            s.BlockedApps, s.BlockedSites,
            s.DeadlineUtc - DateTime.UtcNow);
    }

    public async Task<AckResponse> StartSessionAsync(PipeMessage msg, CancellationToken ct)
    {
        var req = PipeFraming.ParsePayload<StartSessionRequest>(msg);
        if (req is null) return new AckResponse(false, "Invalid request payload.");
        if (req.DeadlineUtc <= DateTime.UtcNow.AddMinutes(1))
            return new AckResponse(false, "Deadline must be at least 1 minute in the future.");
        if (req.DeadlineUtc > DateTime.UtcNow.AddYears(1))
            return new AckResponse(false, "Deadline cannot be more than one year in the future.");
        if (req.Mode == SessionMode.Strict && !req.ConsentGiven)
            return new AckResponse(false, "Consent required for Strict mode.");

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_session?.Status == SessionStatus.Active)
                return new AckResponse(false, "A session is already active.");

            var session = new FocusSession
            {
                Id           = Guid.NewGuid(),
                Mode         = req.Mode,
                Status       = SessionStatus.Active,
                StartedAtUtc = DateTime.UtcNow,
                DeadlineUtc  = req.DeadlineUtc,
                BlockedApps  = req.Apps,
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
            _screenTime.OnSessionStarted();

            _log.LogInformation("Session {Id} started ({Mode}, expires {Deadline:u}).",
                session.Id, session.Mode, session.DeadlineUtc);
            return new AckResponse(true);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task<AckResponse> EndSessionRequestAsync(PipeMessage msg, CancellationToken ct)
    {
        await _sessionLock.WaitAsync(ct);
        try
        {
            var s = _session;
            if (s is null) return new AckResponse(false, "No active session.");
            if (s.Mode == SessionMode.Strict)
                return new AckResponse(false, "Strict mode sessions cannot be ended early.");

            EndSession(s);
            return new AckResponse(true);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public IsBlockedResponse CheckIsBlocked(PipeMessage msg)
    {
        var req = PipeFraming.ParsePayload<IsBlockedRequest>(msg);
        if (req is null || _session is null || _session.Status != SessionStatus.Active)
            return new IsBlockedResponse(false, null, null);

        var screenTimeBlock = _screenTime.GetLaunchBlockResponse(req.ExeName);
        if (screenTimeBlock is not null)
            return screenTimeBlock;

        var match = _session.BlockedApps.FirstOrDefault(a =>
            string.Equals(a.ExeName, req.ExeName, StringComparison.OrdinalIgnoreCase));

        return match is not null
            ? new IsBlockedResponse(
                true,
                _session.DeadlineUtc,
                match.DisplayName,
                $"{match.DisplayName} is blocked until {_session.DeadlineUtc.ToLocalTime():h:mm tt} during this focus session.")
            : new IsBlockedResponse(false, null, null);
    }

    public AckResponse HandleForceReset()
    {
        var s = _session;
        if (s?.Status == SessionStatus.Active)
            return new AckResponse(false, "Cannot reset while a focus session is active. End the session first.");
        try
        {
            _appBlocker.RemoveAllStubIfeoKeys();
            _websiteBlocker.RemoveAllFocusLockBlocks();
            _blockPageServer.Stop();
            SessionRepository.Save(null);
            _session = null;
            _log.LogInformation("Force reset completed.");
            return new AckResponse(true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Force reset failed.");
            return new AckResponse(false, $"Reset failed: {ex.Message}");
        }
    }

    public async Task<AckResponse> HandleSetScreenTimeConfigAsync(PipeMessage msg)
    {
        var s = _session;
        if (s?.Status == SessionStatus.Active && s.Mode == SessionMode.Strict)
            return new AckResponse(false, "Screen Time settings are locked during a Strict mode session.");
        return await _screenTime.HandleSetConfigAsync(msg);
    }

    // ── Session lifecycle ─────────────────────────────────────────────────────

    public void EndSession(FocusSession session)
    {
        _log.LogInformation("Ending session {Id}.", session.Id);

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
        _screenTime.OnSessionEnded();
    }
}
