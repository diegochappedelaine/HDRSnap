using Xunit;

namespace HDRSnap.Tests;

public class HotkeyTableTests
{
    static readonly TrayApp.HotkeyBinding[] Table =
    {
        new(0x0, (uint)Keys.F11, "F11", CaptureMode.Monitor),
        new(0x2, (uint)Keys.F11, "Ctrl+F11", CaptureMode.Window),
    };

    [Theory]
    [InlineData((uint)Keys.F11, 0x0u, 0)]
    [InlineData((uint)Keys.F11, 0x2u, 1)]
    [InlineData((uint)Keys.F11, 0x4u, -1)]   // Shift+F11: no match
    [InlineData((uint)Keys.F11, 0x6u, -1)]   // Ctrl+Shift+F11: no match (exact equality)
    [InlineData((uint)Keys.F12, 0x0u, -1)]
    public void FindHotkey_matches_exact_mods(uint vk, uint mods, int expected)
        => Assert.Equal(expected, TrayApp.FindHotkey(Table, vk, mods));

    [Fact]
    public void BuildHotkeys_defaults_give_screen_and_window()
    {
        var (table, warnings) = TrayApp.BuildHotkeys(new Config());
        Assert.Empty(warnings);
        Assert.Equal(2, table.Length);
        Assert.Equal(CaptureMode.Monitor, table[0].Mode);
        Assert.Equal(CaptureMode.Window, table[1].Mode);
    }

    [Fact]
    public void BuildHotkeys_empty_window_hotkey_disables_it()
    {
        var (table, warnings) = TrayApp.BuildHotkeys(new Config { WindowHotkey = "" });
        Assert.Empty(warnings);
        Assert.Single(table);
    }

    [Fact]
    public void BuildHotkeys_same_hotkeys_disable_window_with_warning()
    {
        var (table, warnings) = TrayApp.BuildHotkeys(new Config { Hotkey = "F11", WindowHotkey = "f11" });
        Assert.Single(table);
        Assert.Single(warnings);
    }

    [Fact]
    public void BuildHotkeys_invalid_main_hotkey_falls_back_to_F11()
    {
        var (table, warnings) = TrayApp.BuildHotkeys(new Config { Hotkey = "PrtSc" });
        Assert.Equal((uint)Keys.F11, table[0].Vk);
        Assert.Equal(0u, table[0].Mods);
        Assert.Single(warnings);
    }
}
