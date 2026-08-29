using System.Text.Json;

namespace ClaudeScope.Core;

/// <summary>
/// 读桌面版写的 plan-usage-history.json，取 5 小时窗口和 7 天窗口的用量百分比。
///
/// 关于这个数据源，必须说清楚三件事（都体现在返回的 UsageView 里）：
///   1. 格式未文档化。文件里带 version 字段，只认识已知版本；不认识就返回 Problem，
///      而不是硬猜——宁可显示「—」，不显示编出来的数字。
///   2. fh/sd 的含义是从行为推断的：fh 取值 0-100 且按 5 小时周期归零，
///      sd 变化缓慢符合 7 天窗口。没有官方确认，所以第一次用应该跟 /usage 对一次数。
///   3. 只有桌面版在跑的时候才更新，采样间隔中位数约 15 分钟，
///      实测见过 11 小时的空档。所以一定要把采样时间交出去，超期就标记 Stale。
/// </summary>
public sealed class UsageReader
{
    readonly ScopeConfig _cfg;
    readonly ScopeLogger _log;

    DateTime _lastRead = DateTime.MinValue;
    DateTime _lastWriteSeen = DateTime.MinValue;
    UsageView? _cached;

    public UsageReader(ScopeConfig cfg, ScopeLogger log)
    {
        _cfg = cfg;
        _log = log;
    }

    /// 文件几分钟才变一次，没必要每次快照都解析 120KB。
    /// 按 mtime 判断要不要重读，最快 10 秒一次。
    public UsageView? Read()
    {
        if (!_cfg.ShowUsage) return null;

        var now = DateTime.UtcNow;
        if (_cached is not null && (now - _lastRead).TotalSeconds < 10)
            return Refresh(_cached, now);

        _lastRead = now;

        FileInfo fi;
        try
        {
            fi = new FileInfo(ScopePaths.PlanUsageFile);
            if (!fi.Exists)
                return _cached = new UsageView { Problem = "桌面版还没写过额度采样文件" };
        }
        catch (Exception ex)
        {
            return _cached = new UsageView { Problem = $"读不到额度文件：{ex.GetType().Name}" };
        }

        if (_cached is not null && fi.LastWriteTimeUtc == _lastWriteSeen)
            return Refresh(_cached, now);

        _lastWriteSeen = fi.LastWriteTimeUtc;

        try
        {
            // 桌面版随时可能在写，用 ReadWrite 共享打开，别把它锁住
            using var fs = new FileStream(ScopePaths.PlanUsageFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;

            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32() : -1;
            if (version != 2)
                return _cached = new UsageView { Problem = $"额度文件格式版本 {version} 不认识，不猜" };

            if (!root.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array)
                return _cached = new UsageView { Problem = "额度文件里没有 samples 数组" };

            // 取最后一条有效采样
            JsonElement? last = null;
            for (var i = samples.GetArrayLength() - 1; i >= 0; i--)
            {
                var s = samples[i];
                if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("t", out _)) { last = s; break; }
            }
            if (last is not { } sample)
                return _cached = new UsageView { Problem = "额度文件里没有可用采样" };

            var t = sample.GetProperty("t").GetInt64();
            int? fh = null, sd = null;
            if (sample.TryGetProperty("u", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                if (u.TryGetProperty("fh", out var f) && f.ValueKind == JsonValueKind.Number) fh = f.GetInt32();
                if (u.TryGetProperty("sd", out var d) && d.ValueKind == JsonValueKind.Number) sd = d.GetInt32();
            }

            if (fh is null && sd is null)
                return _cached = new UsageView { Problem = "采样里没有 fh/sd 字段" };

            // 百分比就该在 0-100。超出范围说明我们对字段的理解错了，宁可不显示
            if (fh is < 0 or > 100 || sd is < 0 or > 100)
                return _cached = new UsageView { Problem = $"额度取值超出 0-100（fh={fh} sd={sd}），不采信" };

            _log.Throttled("usage-ok", "info", $"额度采样已读取：5h={fh}% 周={sd}%");
            _cached = new UsageView { FiveHourPercent = fh, WeekPercent = sd, SampledAt = t };
            return Refresh(_cached, now);
        }
        catch (JsonException ex)
        {
            _log.Throttled("usage-parse", "warn", $"额度文件解析失败：{ex.Message}");
            return _cached = new UsageView { Problem = "额度文件解析失败" };
        }
        catch (IOException ex)
        {
            _log.Throttled("usage-io", "warn", $"额度文件读取失败：{ex.Message}");
            return _cached = new UsageView { Problem = "额度文件读取失败" };
        }
    }

    /// 新鲜度是每次都要重算的——缓存的是数值，不是"它还新鲜"这个判断。
    UsageView Refresh(UsageView v, DateTime now)
    {
        if (v.Problem is not null || v.SampledAt == 0) return v;
        var age = (now - DateTimeOffset.FromUnixTimeMilliseconds(v.SampledAt).UtcDateTime).TotalSeconds;
        return new UsageView
        {
            FiveHourPercent = v.FiveHourPercent,
            WeekPercent = v.WeekPercent,
            SampledAt = v.SampledAt,
            AgeSeconds = age,
            Stale = age > _cfg.Thresholds.UsageStaleMinutes * 60
        };
    }
}
