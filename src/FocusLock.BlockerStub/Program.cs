using System.IO.Pipes;
using System.Windows.Forms;
using FocusLock.Core.Ipc;

// Generic notification mode: launched by the service with --message <title> <body>.
if (args.Length >= 1 && args[0] == "--message")
{
    string title = args.Length >= 2 ? args[1] : "Focus Lock";
    string body  = args.Length >= 3 ? args[2] : string.Empty;
    MessageBox.Show(
        body, title,
        MessageBoxButtons.OK,
        MessageBoxIcon.Warning,
        MessageBoxDefaultButton.Button1,
        MessageBoxOptions.DefaultDesktopOnly);
    return;
}

// Website-blocked notification mode: launched by the service with --notify <domain> <deadline>.
if (args.Length >= 1 && args[0] == "--notify")
{
    string domain   = args.Length >= 2 ? args[1] : "this site";
    string deadline = args.Length >= 3 ? args[2] : "the scheduled time";
    MessageBox.Show(
        $"Focus Lock has blocked access to this site.\n\n{domain} is blocked until {deadline}.",
        "Focus Lock — Access Blocked",
        MessageBoxButtons.OK,
        MessageBoxIcon.Warning,
        MessageBoxDefaultButton.Button1,
        MessageBoxOptions.DefaultDesktopOnly);
    return;
}

// args[0] is the path to the originally-requested executable (passed by Windows via IFEO).
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

    // The reply envelope wraps the actual payload in a nested JSON string.
    var response = PipeFraming.ParsePayload<IsBlockedResponse>(reply);
    if (response?.IsBlocked == true)
    {
        string timeStr = response.DeadlineUtc.HasValue
            ? response.DeadlineUtc.Value.ToLocalTime().ToString("h:mm tt")
            : "the scheduled time";

        string appLabel = response.AppDisplayName ?? exeName;

        MessageBox.Show(
            $"Focus Lock has blocked access to {appLabel}.\n\nThis application is blocked until {timeStr}.",
            "Focus Lock — Access Blocked",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button1,
            MessageBoxOptions.DefaultDesktopOnly);
    }
}
catch
{
    // Fail-open: if the service is unreachable, exit silently.
    // IFEO keys are removed when the session ends, so this path is rare.
}
