using System.Text.Json;

namespace ClaudeUsageProbe;

/// <summary>
/// Loads the OAuth access token from the Claude credentials file
/// (<c>~/.claude/.credentials.json</c>, shape: <c>{ "claudeAiOauth": { "accessToken": "..." } }</c>).
/// <para>
/// The file is opened EXACTLY ONCE (a single shared-read <see cref="FileStream"/> handle) after a
/// link-level reparse/directory reject, validated on THAT handle (bounded size), read with a hard byte
/// cap, and parsed with a streaming <see cref="Utf8JsonReader"/> that extracts ONLY
/// <c>claudeAiOauth.accessToken</c>. The <c>refreshToken</c> is DELIBERATELY never bound to a variable or
/// materialised into a string — it is skipped by the reader — and the temporary byte buffer that
/// transiently held the raw file bytes is zeroed before return (DESIGN §6). The probe holds only the
/// short-lived credential it cannot renew, the least dangerous shape available.
/// </para>
/// <para>
/// Opening once and validating the open handle (rather than validating a <see cref="FileInfo"/> and then
/// re-opening by path) closes the replace/grow TOCTOU race: while the shared-read handle is held no other
/// process may write, grow, replace, or delete the file, so the size and bytes validated are the size and
/// bytes read.
/// </para>
/// </summary>
internal static class AccessTokenSource
{
    /// <summary>64 KiB — the real credentials file is a few hundred bytes; this hard cap bounds how much
    /// is ever read into memory and rejects anything pathological (oversize =&gt; exit 3).</summary>
    private const long MaxFileBytes = 64L * 1024;

    /// <summary>
    /// Attempts to load the access token. On success returns <c>true</c> with <paramref name="token"/>
    /// set and any ACL warning codes in <paramref name="aclWarnings"/> (the read still proceeds). On
    /// failure returns <c>false</c> with a token-free <paramref name="error"/> reason code.
    /// </summary>
    public static bool TryLoadAccessToken(
        string path,
        out string token,
        out string? error,
        out IReadOnlyList<string> aclWarnings)
    {
        token = string.Empty;
        error = null;
        var warnings = new List<string>();
        aclWarnings = warnings;

        FileInfo file;
        try
        {
            file = new FileInfo(path);
        }
        catch
        {
            error = ReasonCodes.CredentialsUnreadable;
            return false;
        }

        if (!file.Exists)
        {
            error = ReasonCodes.CredentialsMissing;
            return false;
        }

        // Link-level guard BEFORE opening: reject a reparse point (symlink/junction) or directory at the
        // credentials path. FileInfo.Attributes reflects the LINK itself, not its target, so this is the
        // one place a symlink can be detected WITHOUT following it — a handle opened normally would already
        // have resolved the link. Everything that governs the BYTES we read (size, content) is validated
        // on the single open handle below.
        try
        {
            var attributes = file.Attributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || (attributes & FileAttributes.Directory) != 0)
            {
                error = ReasonCodes.CredentialsUnreadable;
                return false;
            }
        }
        catch
        {
            error = ReasonCodes.CredentialsUnreadable;
            return false;
        }

        // Open ONCE, shared-read: while this handle is held no other process may write, grow, replace, or
        // delete the file — so the size and bytes validated below are the size and bytes actually read.
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.None,
                });
        }
        catch (FileNotFoundException)
        {
            error = ReasonCodes.CredentialsMissing;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            error = ReasonCodes.CredentialsMissing;
            return false;
        }
        catch
        {
            error = ReasonCodes.CredentialsUnreadable;
            return false;
        }

        byte[]? buffer = null;
        try
        {
            long length;
            try
            {
                length = stream.Length;
            }
            catch
            {
                error = ReasonCodes.CredentialsUnreadable;
                return false;
            }

            // Handle-based size gate: empty or oversize =&gt; decline (exit 3) rather than read something
            // pathological into memory.
            if (length <= 0 || length > MaxFileBytes)
            {
                error = ReasonCodes.CredentialsUnreadable;
                return false;
            }

            // ACL audit is advisory only — never refuse, never modify. A failure to read the ACL is itself
            // a (non-fatal) warning; the read proceeds regardless.
            try
            {
                AclAuditor.Audit(file, warnings);
            }
            catch
            {
                warnings.Add(ReasonCodes.AclUnreadable);
            }

            buffer = new byte[length];
            var totalRead = 0;
            try
            {
                while (totalRead < buffer.Length)
                {
                    var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                    if (read <= 0)
                    {
                        break;
                    }

                    totalRead += read;
                }
            }
            catch
            {
                error = ReasonCodes.CredentialsUnreadable;
                return false;
            }

            if (totalRead <= 0)
            {
                error = ReasonCodes.CredentialsInvalid;
                return false;
            }

            // Extract ONLY claudeAiOauth.accessToken. refreshToken (and every other field) is skipped by
            // the streaming reader and NEVER bound to a variable or materialised into a string.
            try
            {
                if (!TryExtractAccessToken(buffer.AsSpan(0, totalRead), out var value))
                {
                    error = ReasonCodes.CredentialsInvalid;
                    return false;
                }

                token = value;
                return true;
            }
            catch (JsonException)
            {
                error = ReasonCodes.CredentialsInvalid;
                return false;
            }
            catch
            {
                error = ReasonCodes.CredentialsUnreadable;
                return false;
            }
        }
        finally
        {
            // Zero the temporary buffer: it transiently held the raw file bytes (including refreshToken).
            if (buffer is not null)
            {
                Array.Clear(buffer, 0, buffer.Length);
            }

            stream.Dispose();
        }
    }

    /// <summary>
    /// Streams the credentials JSON with a <see cref="Utf8JsonReader"/> and returns ONLY
    /// <c>claudeAiOauth.accessToken</c>. Every other property — crucially <c>refreshToken</c> — is skipped
    /// via <see cref="Utf8JsonReader.Skip"/> and never read into a managed string. Order-independent: a
    /// <c>refreshToken</c> appearing before <c>accessToken</c> is skipped just the same.
    /// </summary>
    internal static bool TryExtractAccessToken(ReadOnlySpan<byte> utf8, out string token)
    {
        token = string.Empty;

        var reader = new Utf8JsonReader(utf8);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return false; // end of root without a claudeAiOauth object
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                return false;
            }

            var isOauth = reader.ValueTextEquals("claudeAiOauth");

            if (!reader.Read())
            {
                return false;
            }

            if (!isOauth)
            {
                reader.Skip(); // skip the whole value of any non-oauth root property
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return false; // claudeAiOauth present but not an object
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return false; // oauth object had no usable accessToken
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return false;
                }

                var isAccessToken = reader.ValueTextEquals("accessToken");

                if (!reader.Read())
                {
                    return false;
                }

                if (!isAccessToken)
                {
                    reader.Skip(); // refreshToken and every other field: skipped, never materialised
                    continue;
                }

                if (reader.TokenType != JsonTokenType.String)
                {
                    return false;
                }

                var value = reader.GetString();
                if (string.IsNullOrEmpty(value))
                {
                    return false;
                }

                token = value;
                return true;
            }

            return false;
        }

        return false;
    }
}
