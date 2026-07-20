using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace ClaudeUsageProbe.Tests;

/// <summary>
/// Creates a throwaway <c>.credentials.json</c> in a temp directory with DISTINCTIVE sentinel access and
/// refresh tokens, so leak assertions ("this exact string must never appear in stdout/stderr") are
/// unambiguous. The refresh-token sentinel exists precisely to prove the probe never reads or sends it.
/// </summary>
internal sealed class TempOAuthFile : IDisposable
{
    public string Path { get; }
    public string AccessToken { get; }
    public string RefreshToken { get; }

    public TempOAuthFile(string? accessToken = null, string? refreshToken = null)
    {
        AccessToken = accessToken ?? "SENTINEL-ACCESS-" + Guid.NewGuid().ToString("N");
        RefreshToken = refreshToken ?? "SENTINEL-REFRESH-" + Guid.NewGuid().ToString("N");

        var directory = Directory.CreateTempSubdirectory("cup-probe-test-").FullName;
        Path = System.IO.Path.Combine(directory, ".credentials.json");

        var json = JsonSerializer.Serialize(new
        {
            claudeAiOauth = new
            {
                accessToken = AccessToken,
                refreshToken = RefreshToken,
                expiresAt = 9999999999999L,
                scopes = new[] { "user:inference" },
            },
        });

        File.WriteAllText(Path, json);

        // Force a deterministic "safe default" ACL so tests are not at the mercy of whatever the machine's
        // temp directory happens to inherit (on a multi-account machine that can include other-user ACEs).
        ApplyCleanProtectedAcl();
    }

    /// <summary>
    /// Replaces the file's DACL with a protected (inheritance-off) ACL containing ONLY the current user,
    /// SYSTEM, and Administrators — exactly the "default profile inheritance" the auditor treats as silent
    /// (DESIGN §6). All inherited ACEs (which may include broad or other-user principals) are dropped.
    /// </summary>
    private void ApplyCleanProtectedAcl()
    {
        var file = new FileInfo(Path);
        var security = file.GetAccessControl();

        // Drop inherited ACEs, then remove any lingering explicit ones, so the DACL is exactly what we add.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule existing in
                 security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRuleSpecific(existing);
        }

        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, AccessControlType.Allow));

        file.SetAccessControl(security);
    }

    /// <summary>
    /// Adds an <c>Everyone:Read</c> ACE on top of the clean default ACL — the broad-permission case the
    /// auditor must warn on (while still proceeding).
    /// </summary>
    public void AddEveryoneReadAce()
    {
        var file = new FileInfo(Path);
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read,
            AccessControlType.Allow));
        file.SetAccessControl(security);
    }

    public void Dispose()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (directory is not null)
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }
}
