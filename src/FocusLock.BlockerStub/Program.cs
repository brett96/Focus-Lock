using System.IO.Pipes;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using FocusLock.Core.Ipc;

const string AppId = "FocusLock.Notification";

static void RegisterAumid()
{
    try
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            $@"SOFTWARE\Classes\AppUserModelId\{AppId}");
        key?.SetValue("DisplayName", "Focus Lock");
    }
    catch { }
}

static string EscXml(string s) =>
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

static async Task ShowToastAsync(string title, string body)
{
    RegisterAumid();
    try
    {
        var xml = new XmlDocument();
        xml.LoadXml($@"<toast duration=""long""><visual><binding template=""ToastGeneric""><text>{EscXml(title)}</text><text>{EscXml(body)}</text></binding></visual></toast>");
        var notifier = ToastNotificationManager.CreateToastNotifier(AppId);
        notifier.Show(new ToastNotification(xml));
    }
    catch { }
    // Brief delay so the notification center receives the request before the process exits.
    await Task.Delay(500);
}

// Generic notification mode: --message <title> <body>
if (args.Length >= 1 && args[0] == "--message")
{
    string title = args.Length >= 2 ? args[1] : "Focus Lock";
    string body  = args.Length >= 3 ? args[2] : string.Empty;
    await ShowToastAsync(title, body);
    return;
}

// Website-blocked notification mode: --notify <domain> <deadline>
if (args.Length >= 1 && args[0] == "--notify")
{
    string domain   = args.Length >= 2 ? args[1] : "this site";
    string deadline = args.Length >= 3 ? args[2] : "the scheduled time";
    await ShowToastAsync("Focus Lock — Access Blocked",
        $"{domain} is blocked until {deadline}.");
    return;
}

// IFEO mode: args[0] is the path to the originally-requested executable (passed by Windows).
string exeName = args.Length > 0 ? Path.GetFileName(args[0]) : string.Empty;

try
{
    using var pipe = new NamedPipeClientStream(".", PipeConstants.PipeName,
        PipeDirection.InOut, PipeOptions.None);

    pipe.Connect(200);

    var request = PipeFraming.BuildRequest(PipeConstants.IsBlocked, new IsBlockedRequest(exeName));
    await PipeFraming.WriteMessageAsync(pipe, request);
    var reply = await PipeFraming.ReadMessageAsync(pipe);

    if (reply is null) return;

    var response = PipeFraming.ParsePayload<IsBlockedResponse>(reply);
    if (response?.IsBlocked == true)
    {
        string body;
        if (!string.IsNullOrWhiteSpace(response.BlockMessage))
        {
            body = response.BlockMessage;
        }
        else
        {
            string timeStr = response.DeadlineUtc.HasValue
                ? response.DeadlineUtc.Value.ToLocalTime().ToString("h:mm tt")
                : "the scheduled time";
            string appLabel = response.AppDisplayName ?? exeName;
            body = $"{appLabel} is blocked until {timeStr}.";
        }

        await ShowToastAsync("Focus Lock — Access Blocked", body);
    }
}
catch
{
    // Fail-open: if the service is unreachable, exit silently.
    // IFEO keys are removed when the session ends, so this path is rare.
}
