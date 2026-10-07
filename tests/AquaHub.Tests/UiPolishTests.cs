using AquaHub.Core.Settings;

namespace AquaHub.Tests;

public sealed class UiPolishTests
{
    [Theory]
    [InlineData("Ctrl+Shift+~", "Ctrl+Shift+Tilde")]
    [InlineData("Ctrl+Shift+Oem3", "Ctrl+Shift+Tilde")]
    [InlineData("Ctrl+Shift+[", "Ctrl+Shift+OpenBracket")]
    [InlineData("Alt+;", "Alt+Semicolon")]
    public void PunctuationShortcutsNormalizeToStableKeyboardKeys(string input, string expected)
    {
        Assert.True(Shortcuts.TryNormalize(input, out var normalized, out _));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("Ctrl+Tab", "Ctrl+Tab", 0x09)]
    [InlineData("Alt+Home", "Alt+Home", 0x24)]
    [InlineData("Ctrl+PageUp", "Ctrl+PageUp", 0x21)]
    [InlineData("Ctrl+ArrowLeft", "Ctrl+Left", 0x25)]
    [InlineData("Ctrl+Backspace", "Ctrl+Backspace", 0x08)]
    [InlineData("Alt+Escape", "Alt+Escape", 0x1B)]
    [InlineData("Ctrl+Delete", "Ctrl+Delete", 0x2E)]
    [InlineData("Ctrl+NumPad5", "Ctrl+NumPad5", 0x65)]
    [InlineData("Ctrl+NumPadAdd", "Ctrl+NumPadAdd", 0x6B)]
    [InlineData("Ctrl+Divide", "Ctrl+NumPadDivide", 0x6F)]
    public void NavigationAndNumpadKeysNormalizeAndMapToOneWindowsKey(string input, string expected, int expectedVk)
    {
        Assert.True(Shortcuts.TryNormalize(input, out var normalized, out _));
        Assert.Equal(expected, normalized);
        Assert.True(Shortcuts.TryGetVirtualKey(normalized.Split('+')[^1], out var virtualKey));
        Assert.Equal(expectedVk, virtualKey);
    }

    [Theory]
    [InlineData("Ctrl+Home+End")]
    [InlineData("Ctrl+NumPad1+NumPad2")]
    [InlineData("Ctrl+Tab+Delete")]
    [InlineData("Ctrl+Backspace+Escape")]
    public void ShortcutWithMoreThanOnePrimaryKeyIsRejected(string gesture)
    {
        Assert.False(Shortcuts.TryNormalize(gesture, out _, out _));
    }
}
