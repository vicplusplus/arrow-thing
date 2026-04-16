namespace ArrowThing.Server.Auth;

/// <summary>
/// Startup-time guards that reject Production boots with missing, short, or known
/// dev-default secrets. Runs before the app listens, so a misconfigured deploy
/// fails fast rather than serving requests with a weak key.
/// </summary>
public static class StartupSecretValidator
{
    public static void ValidateProductionSecret(
        string? value,
        string name,
        int minLength,
        IReadOnlyList<string> blocklist
    )
    {
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException(
                $"{name} must be set in Production. Refusing to start."
            );

        if (value.Length < minLength)
            throw new InvalidOperationException(
                $"{name} must be at least {minLength} characters in Production. Refusing to start."
            );

        foreach (var bad in blocklist)
        {
            if (value == bad)
                throw new InvalidOperationException(
                    $"{name} is set to a known dev default in Production. Refusing to start."
                );
        }
    }
}
