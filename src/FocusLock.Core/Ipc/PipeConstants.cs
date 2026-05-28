namespace FocusLock.Core.Ipc;

public static class PipeConstants
{
    public const string PipeName = "FocusLockService";

    /// <summary>Concurrent pipe clients (UI polls, BlockerStub, etc.).</summary>
    public const int MaxConnections = 16;
    public const int ConnectTimeoutMs = 5000;
    public const int IpcRetryAttempts = 3;
    public const int IpcRetryDelayMs = 150;
    public const int IpcReadTimeoutSeconds = 3;

    // Message type strings
    public const string GetStatus = "GetStatus";
    public const string GetSessionInfo = "GetSessionInfo";
    public const string StartSession = "StartSession";
    public const string EndSession = "EndSession";
    public const string IsBlocked = "IsBlocked";
    public const string GetScreenTimeConfig = "GetScreenTimeConfig";
    public const string SetScreenTimeConfig = "SetScreenTimeConfig";
    public const string GetScreenTimeStatus = "GetScreenTimeStatus";
    public const string ForceReset = "ForceReset";
}
