namespace Platform.Api.Modules.Admin.Services;

/// <summary>
/// Invite acceptance refused without changing Auth credentials or memberships.
/// <see cref="Code"/> is the <c>error</c> string returned to the client.
/// </summary>
public sealed class InviteAcceptanceException : Exception
{
    public InviteAcceptanceException(string code, int statusCode)
        : base(code)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }

    public int StatusCode { get; }
}

public static class InviteAcceptanceCodes
{
    /// <summary>
    /// The email already has a staff Auth user. The caller must sign in as that
    /// user and accept again. The password in the body was not applied.
    /// </summary>
    public const string ExistingAccountSignInRequired = "EXISTING_ACCOUNT_SIGN_IN_REQUIRED";

    /// <summary>
    /// A staff session is present, but it is not the invited email's Auth user.
    /// </summary>
    public const string AccountMismatch = "INVITE_ACCOUNT_MISMATCH";

    /// <summary>
    /// Local memberships and Supabase Auth disagree about which user owns the email.
    /// Nothing is linked and the password is left untouched.
    /// </summary>
    public const string IdentityConflict = "INVITE_IDENTITY_CONFLICT";

    /// <summary>
    /// Preview only: the invite was already accepted. The membership exists; the
    /// client should resume tenant selection (signed in) or send the user to login.
    /// </summary>
    public const string AlreadyAccepted = "INVITE_ALREADY_ACCEPTED";
}
