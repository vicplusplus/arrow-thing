using System.Globalization;

namespace ArrowThing.Server.Auth;

/// <summary>
/// Validates display names for registration and update flows. Enforces length,
/// character-set, and profanity rules. Pure static class — no DB or HTTP deps,
/// directly unit-testable.
/// </summary>
public static class DisplayNameValidator
{
    public const int MinLength = 1;
    public const int MaxLength = 24;

    public const string ErrorLength = "Display name must be 1-24 characters.";
    public const string ErrorInvalidChars = "Display name contains invalid characters.";
    public const string ErrorDisallowed = "Display name contains disallowed words.";

    // Banned tokens are compared against a normalized (lowercased, leet-folded,
    // letters-only) form of the display name. Keep this list short and focused
    // on clear-cut slurs and profanity — a perfect filter would need an
    // external service. Tokens must be lowercase letters only; the folding
    // step strips everything else before comparison.
    private static readonly string[] BannedTokens =
    {
        // Racial, ethnic, and homophobic slurs.
        "nigger",
        "nigga",
        "faggot",
        "fag",
        "tranny",
        "retard",
        "kike",
        "chink",
        "spic",
        "wetback",
        "gook",
        "coon",
        "dyke",
        // Sexual / scatological profanity.
        "fuck",
        "shit",
        "cunt",
        "bitch",
        "bastard",
        "asshole",
        "dickhead",
        "pussy",
        "whore",
        "slut",
        "cock",
        "dildo",
        "jizz",
        "wank",
        // Inappropriate in a public-leaderboard context.
        "rape",
        "rapist",
        "nazi",
        "hitler",
        "pedo",
        "pedophile",
    };

    /// <summary>
    /// Validates a raw display name. Returns Ok=true with the trimmed Normalized
    /// value on success, or Ok=false with a user-facing Error on failure.
    /// Callers should persist the Normalized value, not the original input.
    /// </summary>
    public static (bool Ok, string? Error, string Normalized) Validate(string? raw)
    {
        if (raw == null)
            return (false, ErrorLength, "");

        var trimmed = raw.Trim();

        if (trimmed.Length < MinLength || trimmed.Length > MaxLength)
            return (false, ErrorLength, trimmed);

        if (ContainsInvalidChars(trimmed))
            return (false, ErrorInvalidChars, trimmed);

        if (ContainsBannedToken(trimmed))
            return (false, ErrorDisallowed, trimmed);

        return (true, null, trimmed);
    }

    private static bool ContainsInvalidChars(string s)
    {
        foreach (var c in s)
        {
            // Reject ASCII control chars (includes tab, newline, etc).
            if (c < 0x20 || c == 0x7f)
                return true;

            // Reject Unicode control & format characters — this catches
            // zero-width joiners, non-joiners, RTL overrides, etc., which
            // can be abused for impersonation.
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format)
                return true;
        }
        return false;
    }

    private static bool ContainsBannedToken(string s)
    {
        var normalized = Normalize(s);
        if (normalized.Length == 0)
            return false;

        foreach (var token in BannedTokens)
        {
            if (normalized.Contains(token, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Folds leetspeak substitutions to their letter equivalents, lowercases,
    /// and strips all non-letter characters. The goal is to make substring
    /// matching against the banned list robust to common evasion tactics like
    /// "sh1t", "@sshole", or "n.i.g.g.e.r". Public for direct unit testing.
    /// </summary>
    public static string Normalize(string s)
    {
        var buf = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            var folded = FoldChar(ch);
            if (folded != '\0')
                buf.Append(folded);
        }
        return buf.ToString();
    }

    private static char FoldChar(char c)
    {
        // Leet substitutions → lowercase letter.
        switch (c)
        {
            case '0':
                return 'o';
            case '1':
            case '!':
            case '|':
                return 'i';
            case '3':
                return 'e';
            case '4':
            case '@':
                return 'a';
            case '5':
            case '$':
                return 's';
            case '7':
                return 't';
        }

        // Letters — lowercase.
        if (c >= 'A' && c <= 'Z')
            return (char)(c + 32);
        if (c >= 'a' && c <= 'z')
            return c;

        // Non-letter, non-leet → drop. This means separators like `.`, `_`,
        // `-`, digits we don't leet-fold, and any non-Latin letters are all
        // stripped from the comparison form.
        return '\0';
    }
}
