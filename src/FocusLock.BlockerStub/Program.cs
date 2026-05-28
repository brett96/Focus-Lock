using System.IO.Pipes;
using System.Windows.Forms;
using FocusLock.Core.Ipc;

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
            $"{appLabel} is blocked until {timeStr}.\n\nYour Focus Lock session is active.",
            "Focus Lock",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button1,
            MessageBoxOptions.DefaultDesktopOnly);
    }
}
catch
{
    // Fail-open: if the service is unreachable, exit silently.
    // IFEO keys are removed when the session ends, so this path is rare.
}
