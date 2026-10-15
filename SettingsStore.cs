using System.Text.Json;
using Microsoft.Win32;

namespace EdgeWalker;

/// <summary>設定の保存（%APPDATA%\EdgeWalker\settings.json）</summary>
internal static class SettingsStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EdgeWalker", "settings.json");

    public static Settings Load()
    {
        Settings? s = null;
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
        }
        catch (Exception)
        {
            // 壊れていたら標準の値で起動する
        }
        s ??= new Settings();
        s.Clamp();
        return s;
    }

    public static void Save(Settings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // 保存できなくても動作は続ける
        }
    }
}

/// <summary>Windows起動時の自動起動（現在のユーザーのみ．管理者権限は要らない）</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EdgeWalker";

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue(ValueName, "\"" + exe + "\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // レジストリに書けない環境では何もしない
        }
    }
}
