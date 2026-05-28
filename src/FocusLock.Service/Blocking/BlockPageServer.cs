using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FocusLock.Core.Models;

namespace FocusLock.Service.Blocking;

/// <summary>
/// Binds a minimal HTTP server to 127.0.0.1:80 while a session is active.
/// WebsiteBlocker redirects blocked domains to 127.0.0.1 in the hosts file, so any
/// HTTP request to a blocked site lands here and receives a styled block page.
/// Also spawns a native popup via SessionNotifier (debounced per domain, 30-second cooldown).
/// HTTPS requests cannot be intercepted without a trusted certificate; they will
/// show a browser connection error (still effectively blocked).
/// Fails non-fatally if port 80 is already in use.
/// </summary>
public sealed class BlockPageServer : IDisposable
{
    private readonly ILogger _log;
    private readonly SessionNotifier _notifier;
    private readonly ConcurrentDictionary<string, DateTime> _lastPopup = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public BlockPageServer(ILogger log, SessionNotifier notifier)
    {
        _log      = log;
        _notifier = notifier;
    }

    public void Start(FocusSession session)
    {
        if (_listener is not null || session.BlockedSites.Count == 0) return;
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, 80);
            _listener.Start();
            _ = ServeAsync(session, _cts.Token);
            _log.LogInformation("Block page server started on port 80.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Could not bind block page server to port 80; blocked website requests will show a browser connection error instead of the block page.");
            _cts?.Dispose();
            _cts = null;
            _listener = null;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _lastPopup.Clear();
    }

    private async Task ServeAsync(FocusSession session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                client.ReceiveTimeout = 5000;
                client.SendTimeout    = 5000;
                _ = HandleClientAsync(client, session, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogDebug(ex, "Block page server accept error."); }
        }
    }

    private async Task HandleClientAsync(TcpClient client, FocusSession session, CancellationToken ct)
    {
        using (client)
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

            string? host = null;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                {
                    host = line[5..].Trim();
                    int colon = host.IndexOf(':');
                    if (colon > 0) host = host[..colon];
                }
            }

            string domain   = host ?? "this site";
            string deadline = session.DeadlineUtc.ToLocalTime().ToString("ddd, MMM d 'at' h:mm tt");

            // Windows toast via BlockerStub (debounced to avoid spam on asset-heavy pages).
            if (ShouldNotifyWebsiteBlock(domain))
                _notifier.ShowWebsiteBlocked(domain, deadline);

            byte[] body   = Encoding.UTF8.GetBytes(BuildBlockPage(domain, deadline));
            byte[] header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\n" +
                $"Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                $"Connection: close\r\n\r\n");

            await stream.WriteAsync(header);
            await stream.WriteAsync(body);
        }
        catch { /* client disconnected or timed out */ }
    }

    private bool ShouldNotifyWebsiteBlock(string domain)
    {
        var now = DateTime.UtcNow;
        if (_lastPopup.TryGetValue(domain, out var last) && (now - last).TotalSeconds < 5)
            return false;
        _lastPopup[domain] = now;
        return true;
    }

    // $$""" uses {{ }} for interpolation so bare { } in CSS are literal.
    private static string BuildBlockPage(string domain, string deadline) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>Focus Lock — Access Blocked</title>
          <style>
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body { font-family: -apple-system, "Segoe UI", sans-serif; background: #1E1E2E; color: #CDD6F4; display: flex; align-items: center; justify-content: center; min-height: 100vh; }
            .card { background: #313244; border-radius: 12px; padding: 48px; max-width: 480px; width: 100%; text-align: center; box-shadow: 0 8px 32px rgba(0,0,0,0.4); }
            .lock { font-size: 56px; margin-bottom: 24px; }
            h1 { font-size: 22px; font-weight: 700; margin-bottom: 12px; }
            .domain { color: #F38BA8; font-size: 20px; font-weight: 600; margin: 16px 0; word-break: break-all; }
            .subtitle { color: #A6ADC8; font-size: 14px; line-height: 1.6; }
            .deadline { margin-top: 28px; padding: 14px 20px; background: #1E1E2E; border-radius: 8px; color: #89B4FA; font-size: 14px; }
            .label { font-size: 11px; text-transform: uppercase; letter-spacing: 0.08em; color: #6C7086; margin-bottom: 4px; }
          </style>
        </head>
        <body>
          <div class="card">
            <div class="lock">🔒</div>
            <h1>Focus Lock has blocked access to this site</h1>
            <div class="domain">{{System.Net.WebUtility.HtmlEncode(domain)}}</div>
            <p class="subtitle">This site is blocked during your active focus session.</p>
            <div class="deadline">
              <div class="label">Session ends</div>
              {{deadline}}
            </div>
          </div>
        </body>
        </html>
        """;

    public void Dispose() => Stop();
}
