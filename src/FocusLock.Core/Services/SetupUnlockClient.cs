using System.IO.Pipes;
using FocusLock.Core.Ipc;

namespace FocusLock.Core.Services;

/// <summary>
/// Asks the running Focus Lock service (LocalSystem) to tear down session protection safely.
/// </summary>
public static class SetupUnlockClient
{
    /// <summary>Old services without UnlockForSetup — ends an active regular session in-process.</summary>
    public static (bool Success, string Message) TryEndSessionViaIpc()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                PipeConstants.PipeName,
                PipeDirection.InOut,
                PipeOptions.None);

            pipe.Connect(PipeConstants.ConnectTimeoutMs);

            var request = PipeFraming.BuildRequest(
                PipeConstants.EndSession,
                new EndSessionRequest("Setup unlock"));
            PipeFraming.WriteMessageAsync(pipe, request).GetAwaiter().GetResult();

            var reply = PipeFraming.ReadMessageAsync(pipe).GetAwaiter().GetResult();
            var ack = reply is null ? null : PipeFraming.ParsePayload<AckResponse>(reply);
            if (ack?.Success == true)
                return (true, "Active session ended via IPC.");

            return (false, ack?.ErrorMessage ?? "EndSession was not accepted.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Success, string Message) TryUnlockRunningService()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                PipeConstants.PipeName,
                PipeDirection.InOut,
                PipeOptions.None);

            pipe.Connect(PipeConstants.ConnectTimeoutMs);

            var request = PipeFraming.BuildRequest(PipeConstants.UnlockForSetup);
            PipeFraming.WriteMessageAsync(pipe, request).GetAwaiter().GetResult();

            var reply = PipeFraming.ReadMessageAsync(pipe).GetAwaiter().GetResult();
            if (reply is null)
                return (false, "No response from Focus Lock Service.");

            var ack = PipeFraming.ParsePayload<AckResponse>(reply);
            if (ack is null)
                return (false, "Invalid response from Focus Lock Service.");

            return ack.Success
                ? (true, "Service released protection.")
                : (false, ack.ErrorMessage ?? "Service could not unlock.");
        }
        catch (TimeoutException)
        {
            return (false, "Focus Lock Service did not respond (not running or not reachable).");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
