using ArrowThing.Server.Auth;

namespace ArrowThing.Server.Tests;

public class DisplayNameValidatorTests
{
    [Theory]
    [InlineData("Alice")]
    [InlineData("BobTheBuilder")]
    [InlineData("player_42")]
    [InlineData("mr. mister")]
    [InlineData("日本語")] // non-Latin letters should pass
    [InlineData("Иван")] // Cyrillic should pass
    [InlineData("A")] // min length
    [InlineData("123456789012345678901234")] // 24 chars
    public void Validate_CleanName_Succeeds(string name)
    {
        var (ok, err, _) = DisplayNameValidator.Validate(name);
        Assert.True(ok, $"Expected '{name}' to be valid but got: {err}");
        Assert.Null(err);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")] // only whitespace → trimmed to empty
    [InlineData("1234567890123456789012345")] // 25 chars
    public void Validate_BadLength_Fails(string name)
    {
        var (ok, err, _) = DisplayNameValidator.Validate(name);
        Assert.False(ok);
        Assert.Equal(DisplayNameValidator.ErrorLength, err);
    }

    [Fact]
    public void Validate_Null_Fails()
    {
        var (ok, err, _) = DisplayNameValidator.Validate(null);
        Assert.False(ok);
        Assert.Equal(DisplayNameValidator.ErrorLength, err);
    }

    [Theory]
    [InlineData("Alice\u200bBob")] // zero-width space
    [InlineData("Alice\u200cBob")] // zero-width non-joiner
    [InlineData("Alice\u202eBob")] // RTL override
    [InlineData("Alice\tBob")] // tab
    [InlineData("Alice\nBob")] // newline
    public void Validate_ControlOrFormatChars_Fails(string name)
    {
        var (ok, err, _) = DisplayNameValidator.Validate(name);
        Assert.False(ok);
        Assert.Equal(DisplayNameValidator.ErrorInvalidChars, err);
    }

    [Theory]
    [InlineData("FuckFace")]
    [InlineData("shitlord")]
    [InlineData("n1gger")] // leet
    [InlineData("Sh1tLord")] // leet + case
    [InlineData("@sshole")] // leet @
    [InlineData("b!tch")] // leet !
    [InlineData("n.i.g.g.e.r")] // separators stripped
    [InlineData("  FUCK  ")] // trimmed but still banned
    public void Validate_Profanity_Fails(string name)
    {
        var (ok, err, _) = DisplayNameValidator.Validate(name);
        Assert.False(ok);
        Assert.Equal(DisplayNameValidator.ErrorDisallowed, err);
    }

    [Fact]
    public void Validate_TrimsWhitespace()
    {
        var (ok, _, normalized) = DisplayNameValidator.Validate("  Alice  ");
        Assert.True(ok);
        Assert.Equal("Alice", normalized);
    }

    [Fact]
    public void Validate_TrimReducesToMaxLength()
    {
        // 24 chars of content with leading/trailing whitespace — should pass after trim.
        var name = "   " + new string('a', 24) + "   ";
        var (ok, _, normalized) = DisplayNameValidator.Validate(name);
        Assert.True(ok);
        Assert.Equal(24, normalized.Length);
    }

    [Fact]
    public void Normalize_StripsNonLetters()
    {
        // Separators and digits we don't fold are dropped.
        Assert.Equal("abc", DisplayNameValidator.Normalize("a-b_c"));
        Assert.Equal("abc", DisplayNameValidator.Normalize("a.b.c"));
        Assert.Equal("abc", DisplayNameValidator.Normalize("a b c"));
    }

    [Fact]
    public void Normalize_FoldsLeetSpeak()
    {
        Assert.Equal("shit", DisplayNameValidator.Normalize("sh1t"));
        Assert.Equal("ass", DisplayNameValidator.Normalize("@$$"));
        Assert.Equal("elite", DisplayNameValidator.Normalize("3l1t3"));
    }
}
