using NUnit.Framework;
using UnityEngine.TextCore.LowLevel;

/// <summary>
/// Verifies that the committed Noto Sans font files contain the expected
/// glyph coverage for broad Unicode rendering (display names, lobby names).
/// Tests the raw .ttf/.otf files via FontEngine, independent of whether
/// Font Assets have been created in the Editor.
/// </summary>
[TestFixture]
public class FontCoverageTests
{
    private const string FontDir = "Assets/Fonts/";

    [OneTimeSetUp]
    public void InitEngine()
    {
        FontEngine.InitializeFontEngine();
    }

    // -- Primary font: Latin, Greek, Cyrillic --

    [TestCase('A', "Latin A")]
    [TestCase('z', "Latin z")]
    [TestCase('0', "Digit 0")]
    [TestCase(0x03A9, "Greek Omega")]
    [TestCase(0x0414, "Cyrillic De")]
    public void NotoSans_HasGlyph(int codePoint, string description)
    {
        AssertFontHasGlyph("NotoSans-Regular.ttf", codePoint, description);
    }

    // -- CJK (Japanese subset) --

    [TestCase(0x5C71, "CJK mountain")]
    [TestCase(0x7530, "CJK rice field")]
    [TestCase(0x30A2, "Katakana a")]
    public void NotoSansJP_HasGlyph(int codePoint, string description)
    {
        AssertFontHasGlyph("NotoSansJP-Regular.otf", codePoint, description);
    }

    // -- Arabic --

    [TestCase(0x0645, "Arabic meem")]
    [TestCase(0x0627, "Arabic alef")]
    public void NotoSansArabic_HasGlyph(int codePoint, string description)
    {
        AssertFontHasGlyph("NotoSansArabic-Regular.ttf", codePoint, description);
    }

    // -- Hebrew --

    [TestCase(0x05E9, "Hebrew shin")]
    [TestCase(0x05DC, "Hebrew lamed")]
    public void NotoSansHebrew_HasGlyph(int codePoint, string description)
    {
        AssertFontHasGlyph("NotoSansHebrew-Regular.ttf", codePoint, description);
    }

    // -- Thai --

    [TestCase(0x0E2A, "Thai so sua")]
    [TestCase(0x0E27, "Thai wo waen")]
    public void NotoSansThai_HasGlyph(int codePoint, string description)
    {
        AssertFontHasGlyph("NotoSansThai-Regular.ttf", codePoint, description);
    }

    // -- Devanagari --

    [TestCase(0x0928, "Devanagari na")]
    [TestCase(0x092E, "Devanagari ma")]
    public void NotoSansDevanagari_HasGlyph(int codePoint, string description)
    {
        AssertFontHasGlyph("NotoSansDevanagari-Regular.ttf", codePoint, description);
    }

    // -- Emoji (monochrome) --

    [TestCase(0x1F44B, "Waving hand")]
    [TestCase(0x1F3AE, "Video game controller")]
    public void NotoEmoji_HasGlyph(int codePoint, string description)
    {
        AssertFontHasGlyph("NotoEmoji-Regular.ttf", codePoint, description);
    }

    // -- Bold variant --

    [TestCase('A', "Latin A")]
    [TestCase(0x03A9, "Greek Omega")]
    public void NotoSansBold_HasGlyph(int codePoint, string description)
    {
        AssertFontHasGlyph("NotoSans-Bold.ttf", codePoint, description);
    }

    private static void AssertFontHasGlyph(string fileName, int codePoint, string description)
    {
        string path = FontDir + fileName;
        var result = FontEngine.LoadFontFace(path);
        Assert.That(
            result,
            Is.EqualTo(FontEngineError.Success),
            $"Failed to load font {fileName}: {result}"
        );

        bool found = FontEngine.TryGetGlyphIndex((uint)codePoint, out uint glyphIndex);
        Assert.That(
            found && glyphIndex != 0,
            Is.True,
            $"{fileName} missing {description} (U+{codePoint:X4})"
        );
    }
}
