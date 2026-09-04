namespace WinIMsg.Core.Ssh;

public sealed record MacContactsAuthorizationResult(
    string State,
    string RawValue,
    string Detail)
{
    public bool IsAuthorized => State.Equals("authorized", StringComparison.OrdinalIgnoreCase);

    public bool IsNotDetermined => State.Equals("notDetermined", StringComparison.OrdinalIgnoreCase);

    public bool IsDeniedOrRestricted =>
        State.Equals("denied", StringComparison.OrdinalIgnoreCase) ||
        State.Equals("restricted", StringComparison.OrdinalIgnoreCase);

    public static MacContactsAuthorizationResult Unknown(string detail) =>
        new("unknown", string.Empty, detail);
}
