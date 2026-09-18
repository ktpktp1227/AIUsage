using System;
using System.IO;
using System.Text.Json;

namespace AIUsage;

public sealed class Settings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Topmost { get; set; } = true;
    public int RefreshMinutes { get; set; } = 5;

    private static string FilePath => Path.Combine(UsageProbe.DataDirectory, "settings.json");

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(UsageProbe.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // 설정 저장 실패는 무시
        }
    }
}
