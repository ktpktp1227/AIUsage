using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AIUsage;

public sealed record UsageWindow(string Key, string Name, double Percent, DateTimeOffset? ResetsAt);

public sealed record UsageSnapshot(IReadOnlyList<UsageWindow> Windows, DateTimeOffset UpdatedAt);

public sealed class UsageException(string message, string? detail = null) : Exception(message)
{
    public string? Detail { get; } = detail;
}

/// <summary>
/// Claude Code CLI의 내장 명령 <c>claude -p "/usage" --no-session-persistence</c> 를 실행해
/// 플랜 사용량을 읽는다. 모델 요청이 없어 토큰을 쓰지 않고, 세션 기록도 남기지 않는다.
/// 로그인 정보는 CLI가 스스로 처리하며, 이 프로그램은 출력 텍스트만 읽는다.
/// </summary>
public static class UsageProbe
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIUsage");

    private static string CachePath => Path.Combine(DataDirectory, "last-usage.json");

    public const string CliMissing = "CLI 없음";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    // 예: "Current session: 27% used · resets Sep 17, 6:20am (Asia/Seoul)"
    //     "Current week (all models): 5% used · resets Sep 23, 12pm (Asia/Seoul)"
    private static readonly Regex UsageLine = new(
        @"^\s*Current\s+(?<label>session|week)\s*(?:\((?<scope>[^)]*)\))?\s*:\s*(?<pct>\d+(?:\.\d+)?)\s*%\s*used(?:.*?\bresets\s+(?<reset>.+?))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public static async Task<UsageSnapshot> FetchAsync()
    {
        var claude = ResolveClaude() ?? throw new UsageException(
            CliMissing, "claude 명령을 찾을 수 없습니다. Claude Code CLI를 설치하고 로그인해 주세요.");

        Directory.CreateDirectory(DataDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = claude.FileName,
            // 프로젝트 설정(hooks, MCP 등)을 불러오지 않도록 빈 데이터 폴더에서 실행
            WorkingDirectory = DataDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in claude.PrefixArgs)
            psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("/usage");
        psi.ArgumentList.Add("--no-session-persistence");
        psi.Environment["NO_COLOR"] = "1";

        using var process = Process.Start(psi) ?? throw new UsageException("실행 실패", "claude 프로세스를 시작하지 못했습니다.");
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new UsageException("시간 초과", "claude 명령이 60초 안에 끝나지 않았습니다.");
        }

        string stdout = AnsiEscape.Replace(await stdoutTask, "");
        string stderr = AnsiEscape.Replace(await stderrTask, "");

        var snapshot = Parse(stdout, DateTimeOffset.Now);
        if (snapshot.Windows.Count == 0)
        {
            string reason = FirstLine(stdout) ?? FirstLine(stderr) ?? "출력이 비어 있습니다.";
            throw new UsageException("사용량 없음", "플랜 사용량을 읽지 못했습니다: " + reason);
        }

        SaveCache(snapshot);
        return snapshot;
    }

    internal static UsageSnapshot Parse(string output, DateTimeOffset now)
    {
        var windows = new List<UsageWindow>();
        foreach (Match m in UsageLine.Matches(output))
        {
            string scope = m.Groups["scope"].Value.Trim();
            string key, name;
            if (m.Groups["label"].Value.Equals("session", StringComparison.OrdinalIgnoreCase))
            {
                key = "session";
                name = "세션";
            }
            else if (scope.Length == 0 || scope.Equals("all models", StringComparison.OrdinalIgnoreCase))
            {
                key = "week";
                name = "주간";
            }
            else
            {
                // 예: "Sonnet only" → "Sonnet"
                key = "week:" + scope;
                name = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            }

            double percent = double.Parse(m.Groups["pct"].Value, CultureInfo.InvariantCulture);
            DateTimeOffset? resetsAt = m.Groups["reset"].Success ? ParseReset(m.Groups["reset"].Value, now) : null;
            windows.Add(new UsageWindow(key, name, percent, resetsAt));

            if (windows.Count == 3) break;
        }
        return new UsageSnapshot(windows, now);
    }

    /// <summary>"Sep 17, 6:20am (Asia/Seoul)", "Sep 23, 12pm (Asia/Seoul)", "6:20am" 형식을 해석한다.</summary>
    internal static DateTimeOffset? ParseReset(string text, DateTimeOffset now)
    {
        text = text.Trim();
        var zone = TimeZoneInfo.Local;

        var zoneMatch = Regex.Match(text, @"\(([^)]+)\)\s*$");
        if (zoneMatch.Success)
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneMatch.Groups[1].Value.Trim()); } catch { }
            text = text[..zoneMatch.Index].Trim();
        }
        text = Regex.Replace(text.TrimEnd('.', ' '), @"(?i)(am|pm)$", x => x.Value.ToUpperInvariant());

        var nowInZone = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        string[] withDate = ["MMM d, h:mmtt", "MMM d, htt", "MMM d h:mmtt", "MMM d htt", "MMM d, H:mm", "MMM d"];
        string[] timeOnly = ["h:mmtt", "htt", "H:mm"];
        const DateTimeStyles style = DateTimeStyles.AllowWhiteSpaces;

        DateTime local;
        if (DateTime.TryParseExact(text, withDate, CultureInfo.InvariantCulture, style, out var parsed))
        {
            local = new DateTime(nowInZone.Year, parsed.Month, parsed.Day, parsed.Hour, parsed.Minute, 0);
            if (local < nowInZone.AddDays(-1))
                local = local.AddYears(1);
        }
        else if (DateTime.TryParseExact(text, timeOnly, CultureInfo.InvariantCulture, style, out parsed))
        {
            local = nowInZone.Date + parsed.TimeOfDay;
            if (local < nowInZone)
                local = local.AddDays(1);
        }
        else
        {
            return null;
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static string? FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed.Length > 120 ? trimmed[..120] + "…" : trimmed;
        }
        return null;
    }

    private static (string FileName, string[] PrefixArgs)? ResolveClaude()
    {
        var dirs = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            dirs.Add(dir.Trim().Trim('"'));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));

        foreach (var dir in dirs)
        {
            try
            {
                var exe = Path.Combine(dir, "claude.exe");
                if (File.Exists(exe)) return (exe, []);
            }
            catch { }
        }
        // npm 전역 설치는 claude.cmd 로 제공된다
        foreach (var dir in dirs)
        {
            try
            {
                var cmd = Path.Combine(dir, "claude.cmd");
                if (File.Exists(cmd)) return ("cmd.exe", ["/d", "/c", cmd]);
            }
            catch { }
        }
        return null;
    }

    // ───── 마지막 값 캐시 (다음 실행 때 바로 표시) ─────

    public static UsageSnapshot? LoadCache()
    {
        try
        {
            return JsonSerializer.Deserialize<UsageSnapshot>(File.ReadAllText(CachePath));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(UsageSnapshot snapshot)
    {
        try
        {
            File.WriteAllText(CachePath, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
            // 캐시 저장 실패는 무시
        }
    }
}
