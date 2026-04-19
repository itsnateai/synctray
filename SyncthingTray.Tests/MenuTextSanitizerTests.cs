namespace SyncthingTray.Tests;

[TestClass]
public class MenuTextSanitizerTests
{
    [TestMethod]
    public void Sanitize_NullOrEmpty_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, MenuTextSanitizer.Sanitize(null));
        Assert.AreEqual(string.Empty, MenuTextSanitizer.Sanitize(string.Empty));
    }

    [TestMethod]
    public void Sanitize_PlainText_Unchanged()
    {
        Assert.AreEqual("Dropbox", MenuTextSanitizer.Sanitize("Dropbox"));
        Assert.AreEqual("My Photos 2024", MenuTextSanitizer.Sanitize("My Photos 2024"));
    }

    [TestMethod]
    public void Sanitize_Ampersand_IsEscaped()
    {
        // WinForms would otherwise treat `&D` as the keyboard mnemonic for D.
        Assert.AreEqual("&&Dropbox", MenuTextSanitizer.Sanitize("&Dropbox"));
        Assert.AreEqual("A && B", MenuTextSanitizer.Sanitize("A & B"));
        Assert.AreEqual("&&&&double", MenuTextSanitizer.Sanitize("&&double"));
    }

    [TestMethod]
    public void Sanitize_CRLF_IsStripped()
    {
        // Protects TrayLog interpolation sites from log-line injection.
        Assert.AreEqual("abc", MenuTextSanitizer.Sanitize("a\rb\nc"));
        Assert.AreEqual("onetwo", MenuTextSanitizer.Sanitize("one\r\ntwo"));
    }

    [TestMethod]
    public void Sanitize_Control_AndNull_AreStripped()
    {
        Assert.AreEqual("ab", MenuTextSanitizer.Sanitize("a\0b"));
        Assert.AreEqual("ab", MenuTextSanitizer.Sanitize("a\u0007b"));   // BEL
        Assert.AreEqual("ab", MenuTextSanitizer.Sanitize("a\u007Fb"));   // DEL
        Assert.AreEqual("ab", MenuTextSanitizer.Sanitize("a\tb"));        // TAB
    }

    [TestMethod]
    public void Sanitize_Unicode_AboveC1_Preserved()
    {
        Assert.AreEqual("café", MenuTextSanitizer.Sanitize("café"));
        Assert.AreEqual("東京", MenuTextSanitizer.Sanitize("東京"));
        Assert.AreEqual("📁 photos", MenuTextSanitizer.Sanitize("📁 photos"));
    }

    [TestMethod]
    public void Sanitize_TooLong_TruncatedWithEllipsis()
    {
        var input = new string('a', MenuTextSanitizer.MaxLength + 50);
        var output = MenuTextSanitizer.Sanitize(input);
        Assert.AreEqual(MenuTextSanitizer.MaxLength + 1, output.Length); // + ellipsis
        Assert.IsTrue(output.EndsWith('…'));
    }

    [TestMethod]
    public void Sanitize_ExactlyAtLimit_NoEllipsis()
    {
        var input = new string('a', MenuTextSanitizer.MaxLength);
        var output = MenuTextSanitizer.Sanitize(input);
        Assert.AreEqual(MenuTextSanitizer.MaxLength, output.Length);
        Assert.IsFalse(output.EndsWith('…'));
    }

    [TestMethod]
    public void Sanitize_CombinedAttack_AllDefensesFire()
    {
        // Hostile label: mnemonic steal + log-line injection + length bomb
        var hostile = "&Admin\r\n[FAKE] error: all good" + new string('x', 500);
        var output = MenuTextSanitizer.Sanitize(hostile);
        Assert.IsFalse(output.Contains('\r'));
        Assert.IsFalse(output.Contains('\n'));
        Assert.IsTrue(output.StartsWith("&&Admin"));
        // Ampersand doubling counts one logical char but writes two chars, so
        // the hard ceiling on rendered length is 2×MaxLength + 1 (ellipsis).
        Assert.IsTrue(output.Length <= MenuTextSanitizer.MaxLength * 2 + 1);
    }
}
