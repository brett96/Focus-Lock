using System.DirectoryServices.AccountManagement;
using System.Security.Cryptography;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;
using Microsoft.Win32;

namespace FocusLock.Service.Blocking;

public class StrictModeManager(ILogger log)
{
    private const string AdminAccountName = "FocusLockAdmin";
    private const string SpecialAccountsKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList";

    public AckResponse Activate(FocusSession session)
    {
        try
        {
            if (IsDomainJoined())
                return new AckResponse(false, "Strict mode is only supported on non-domain-joined machines.");

            CleanupStaleFocusLockAdmin();

            string password = GeneratePassword();

            CreateAdminAccount(password);
            HideFromLoginScreen();

            // Encrypt and store the password. The service enforces the time-lock in code.
            session.StrictPasswordBlob = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(password),
                null,
                DataProtectionScope.LocalMachine);

            // Build the list BEFORE demoting — crash-safe ordering.
            using var ctx = new PrincipalContext(ContextType.Machine);
            using var adminsGroup = GroupPrincipal.FindByIdentity(ctx, "Administrators")
                ?? throw new InvalidOperationException("Cannot find Administrators group.");

            var toRemove = adminsGroup.Members
                .OfType<UserPrincipal>()
                .Where(u => !u.Name.Equals(AdminAccountName, StringComparison.OrdinalIgnoreCase))
                .Select(u => u.Name)
                .ToList();

            session.DemotedAccountNames.AddRange(toRemove);

            // DemotedAccountNames is now populated — session.json will be saved by caller BEFORE demotion.
            // (The caller calls SessionRepository.Save right after this method returns.)

            // Demote all current admins.
            foreach (var name in toRemove)
            {
                var user = UserPrincipal.FindByIdentity(ctx, name);
                if (user is null) continue;
                adminsGroup.Members.Remove(user);
                log.LogInformation("Demoted {User} to standard user.", name);
            }
            adminsGroup.Save();

            log.LogInformation("Strict mode activated. {Count} accounts demoted.", toRemove.Count);
            return new AckResponse(true);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to activate Strict mode.");
            return new AckResponse(false, $"Strict mode setup failed: {ex.Message}");
        }
    }

    public void Deactivate(FocusSession session)
    {
        try
        {
            using var ctx = new PrincipalContext(ContextType.Machine);
            using var adminsGroup = GroupPrincipal.FindByIdentity(ctx, "Administrators");

            if (adminsGroup is not null)
            {
                foreach (var name in session.DemotedAccountNames)
                {
                    try
                    {
                        var user = UserPrincipal.FindByIdentity(ctx, name);
                        if (user is not null && !adminsGroup.Members.Contains(user))
                        {
                            adminsGroup.Members.Add(user);
                            log.LogInformation("Restored {User} to Administrators.", name);
                        }
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Failed to restore {User}.", name);
                    }
                }
                adminsGroup.Save();
            }

            DeleteFocusLockAdmin(ctx);
            RemoveFromLoginScreenHide();

            session.StrictPasswordBlob = null;
            log.LogInformation("Strict mode deactivated.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error deactivating Strict mode.");
        }
    }

    private static bool IsDomainJoined()
    {
        try
        {
            System.DirectoryServices.ActiveDirectory.Domain.GetComputerDomain();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GeneratePassword()
    {
        byte[] raw = new byte[48];
        RandomNumberGenerator.Fill(raw);
        return Convert.ToBase64String(raw);
    }

    private void CreateAdminAccount(string password)
    {
        using var ctx = new PrincipalContext(ContextType.Machine);
        using var user = new UserPrincipal(ctx)
        {
            Name = AdminAccountName,
            DisplayName = AdminAccountName,
            Description = "Focus Lock system account — do not modify",
            PasswordNeverExpires = true,
            UserCannotChangePassword = true,
            Enabled = true
        };
        user.SetPassword(password);
        user.Save();

        using var adminsGroup = GroupPrincipal.FindByIdentity(ctx, "Administrators")
            ?? throw new InvalidOperationException("Cannot find Administrators group.");
        adminsGroup.Members.Add(user);
        adminsGroup.Save();

        log.LogInformation("FocusLockAdmin account created and added to Administrators.");
    }

    private static void HideFromLoginScreen()
    {
        using var key = Registry.LocalMachine.CreateSubKey(SpecialAccountsKey, writable: true);
        key.SetValue(AdminAccountName, 0, RegistryValueKind.DWord);
    }

    private static void RemoveFromLoginScreenHide()
    {
        using var key = Registry.LocalMachine.OpenSubKey(SpecialAccountsKey, writable: true);
        key?.DeleteValue(AdminAccountName, throwOnMissingValue: false);
    }

    private void CleanupStaleFocusLockAdmin()
    {
        try
        {
            using var ctx = new PrincipalContext(ContextType.Machine);
            DeleteFocusLockAdmin(ctx);
            RemoveFromLoginScreenHide();
        }
        catch { /* no stale account */ }
    }

    private void DeleteFocusLockAdmin(PrincipalContext ctx)
    {
        var account = UserPrincipal.FindByIdentity(ctx, AdminAccountName);
        if (account is null) return;
        account.Delete();
        log.LogInformation("FocusLockAdmin account deleted.");
    }
}
