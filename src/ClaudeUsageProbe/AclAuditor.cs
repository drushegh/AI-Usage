using System.Security.AccessControl;
using System.Security.Principal;

namespace ClaudeUsageProbe;

/// <summary>
/// Audits the ACL of the credentials file (DESIGN §6, "warn-and-proceed"). This NEVER refuses to read
/// and NEVER modifies the file or its permissions — it only classifies who can read the file and adds a
/// token-free warning code when the ACL is broader than the safe default.
/// <list type="bullet">
///   <item>OK (silent): the file Owner, the current user, SYSTEM, and Administrators — the default
///   Windows profile inheritance. Flagging these would train alarm fatigue.</item>
///   <item>WARN: Everyone, Users, Authenticated Users, or any other named principal.</item>
/// </list>
/// </summary>
internal static class AclAuditor
{
    /// <summary>
    /// Appends one <c>acl-permissive:&lt;class&gt;</c> code per distinct broad principal found in an
    /// <see cref="AccessControlType.Allow"/> rule. Only the identity CLASS is reported (everyone / users
    /// / authenticated-users / other-user) — never a raw account name — so no PII and no token can leak.
    /// </summary>
    public static void Audit(FileInfo file, List<string> warnings)
    {
        var security = file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);

        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

        SecurityIdentifier? currentUser = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            currentUser = identity.User;
        }
        catch
        {
            // Current user is a best-effort refinement of the OK set; its absence only makes the audit
            // slightly more conservative (may warn on the current user's own ACE), never less safe.
        }

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        var flagged = new SortedSet<string>(StringComparer.Ordinal);

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            if (rule.IdentityReference is not SecurityIdentifier sid)
            {
                continue;
            }

            if (sid == system || sid == administrators)
            {
                continue;
            }

            if (owner is not null && sid == owner)
            {
                continue;
            }

            if (currentUser is not null && sid == currentUser)
            {
                continue;
            }

            if (sid == everyone)
            {
                flagged.Add("everyone");
            }
            else if (sid == users)
            {
                flagged.Add("users");
            }
            else if (sid == authenticatedUsers)
            {
                flagged.Add("authenticated-users");
            }
            else
            {
                flagged.Add("other-user");
            }
        }

        foreach (var identityClass in flagged)
        {
            warnings.Add(ReasonCodes.AclPermissive + ":" + identityClass);
        }
    }
}
