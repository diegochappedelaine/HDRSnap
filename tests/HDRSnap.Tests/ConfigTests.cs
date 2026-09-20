using Xunit;

namespace HDRSnap.Tests;

public class ConfigTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "HDRSnapTests", Guid.NewGuid().ToString("N"));
    string CfgPath => Path.Combine(_dir, "config.json");

    public ConfigTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Load_missing_file_writes_defaults()
    {
        var cfg = Config.Load(CfgPath);
        Assert.True(File.Exists(CfgPath));
        Assert.Equal("F11", cfg.Hotkey);
        Assert.Null(Config.LoadWarning);
    }

    [Fact]
    public void Load_corrupt_json_returns_defaults_and_keeps_file()
    {
        const string garbage = "{ \"Hotkey\": \"Ctrl+F12\", ";   // truncated JSON
        File.WriteAllText(CfgPath, garbage);
        var cfg = Config.Load(CfgPath);
        Assert.Equal("F11", cfg.Hotkey);
        Assert.Equal(garbage, File.ReadAllText(CfgPath));      // not overwritten
        Assert.NotNull(Config.LoadWarning);
    }

    [Fact]
    public void Load_null_hotkey_falls_back_but_keeps_other_values()
    {
        File.WriteAllText(CfgPath, "{ \"Hotkey\": null, \"PlaySound\": false, \"OutputDir\": \"\" }");
        var cfg = Config.Load(CfgPath);
        Assert.Equal("F11", cfg.Hotkey);
        Assert.False(cfg.PlaySound);
        Assert.False(string.IsNullOrWhiteSpace(cfg.OutputDir));
        Assert.Null(Config.LoadWarning);
    }

    [Theory]
    [InlineData("F11", 0x0u, (uint)Keys.F11)]
    [InlineData("Ctrl+Shift+F11", 0x6u, (uint)Keys.F11)]
    [InlineData(" ctrl + f12 ", 0x2u, (uint)Keys.F12)]
    [InlineData("Alt+Win+PrintScreen", 0x9u, (uint)Keys.PrintScreen)]
    public void TryParseHotkey_accepts_valid(string text, uint mods, uint vk)
    {
        var cfg = new Config { Hotkey = text };
        Assert.True(cfg.TryParseHotkey(out var m, out var k, out var err), err);
        Assert.Equal(mods, m);
        Assert.Equal(vk, k);
    }

    [Theory]
    [InlineData("PrtSc")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+F1+F2")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseHotkey_rejects_invalid(string text)
    {
        var cfg = new Config { Hotkey = text };
        Assert.False(cfg.TryParseHotkey(out _, out _, out var err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Theory]
    [InlineData("Ctrl+1")]
    [InlineData("Ctrl+0")]
    [InlineData("Modifiers")]
    public void TryParseHotkey_rejects_numeric_and_unreachable(string text)
    {
        var cfg = new Config { Hotkey = text };
        Assert.False(cfg.TryParseHotkey(out _, out var vk, out var err), $"accepted with vk={vk}");
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void Save_then_Load_round_trips()
    {
        var cfg = new Config { Hotkey = "Ctrl+F12", WindowHotkey = "Alt+F11", OutputDir = @"D:\Shots", PlaySound = false, TonemapHeadroom = 2f, CopyToClipboard = false };
        Assert.True(cfg.Save(CfgPath));
        var back = Config.Load(CfgPath);
        Assert.Equal("Ctrl+F12", back.Hotkey);
        Assert.Equal("Alt+F11", back.WindowHotkey);
        Assert.Equal(@"D:\Shots", back.OutputDir);
        Assert.False(back.PlaySound);
        Assert.Equal(2f, back.TonemapHeadroom);
        Assert.False(back.CopyToClipboard);
    }

    [Fact]
    public void Save_creates_missing_directory()
    {
        string nested = Path.Combine(_dir, "a", "b", "config.json");
        Assert.True(new Config().Save(nested));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Save_backs_up_a_config_that_Load_could_not_read()
    {
        const string garbage = "{ \"Hotkey\": \"Ctrl+F12\", ";   // truncated JSON
        File.WriteAllText(CfgPath, garbage);
        var cfg = Config.Load(CfgPath);            // keeps the file, returns defaults
        cfg.OutputDir = @"D:\Ailleurs";

        Assert.True(cfg.Save(CfgPath));
        Assert.Equal(garbage, File.ReadAllText(CfgPath + ".bak"));   // the original is saved
        Assert.Equal(@"D:\Ailleurs", Config.Load(CfgPath).OutputDir);
    }

    [Fact]
    public void Save_of_a_loaded_config_makes_no_backup()
    {
        Assert.True(new Config { OutputDir = @"D:\Un" }.Save(CfgPath));
        var cfg = Config.Load(CfgPath);            // read back correctly
        cfg.OutputDir = @"D:\Deux";
        Assert.True(cfg.Save(CfgPath));
        Assert.False(File.Exists(CfgPath + ".bak"));
    }

    [Fact]
    public void CopyToClipboard_defaults_to_true() => Assert.True(new Config().CopyToClipboard);

    [Fact]
    public void CopyFrom_copies_every_settable_property()
    {
        var src = new Config();
        foreach (var p in typeof(Config).GetProperties().Where(p => p.CanWrite && !p.GetMethod.IsStatic))
        {
            object v = p.GetValue(src);
            p.SetValue(src, v switch
            {
                bool b => !b,
                string s => "x_" + s,
                float f => f + 1f,
                _ => throw new Exception($"type not handled in the test: {p.Name} ({p.PropertyType})"),
            });
        }
        var dst = new Config();
        dst.CopyFrom(src);
        foreach (var p in typeof(Config).GetProperties().Where(p => p.CanWrite && !p.GetMethod.IsStatic))
            Assert.True(Equals(p.GetValue(src), p.GetValue(dst)), $"{p.Name} was not copied");
    }
}
