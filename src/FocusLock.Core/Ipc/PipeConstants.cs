namespace FocusLock.Core.Ipc;

public static class PipeConstants
{
    public const string PipeName = "FocusLockService";
    public const int MaxConnections = 4;
    public const int ConnectTimeoutMs = 2000;

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
