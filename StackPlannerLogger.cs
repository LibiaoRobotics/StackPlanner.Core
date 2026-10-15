using System.Globalization;
using System.Text;

namespace StackPlanner.Core;

/// <summary>
/// Core 内部日志器：支持内存环形缓冲与可选文件落盘。
/// </summary>
internal sealed class StackPlannerLogger
{
    private const int DefaultMaxBufferedEntries = 1000;
    private readonly object _sync = new();
    private readonly List<PlannerLogEntry> _entries = new();
    private PlannerLogOptions _options = new();

    public PlannerLogOptions CurrentOptions
    {
        get
        {
            lock (_sync)
                return CloneOptions(_options);
        }
    }

    public void Configure(PlannerLogOptions? options)
    {
        lock (_sync)
        {
            _options = NormalizeOptions(options);
            TrimEntriesUnsafe(_options.MaxBufferedEntries);
        }

        Log(
            PlannerLogLevel.Info,
            "logging.configured",
            "Core 日志配置已更新。",
            $"Enabled={_options.Enabled}, MinLevel={_options.MinLevel}, MaxBufferedEntries={_options.MaxBufferedEntries}, FilePath={_options.FilePath}");
    }

    public void Clear()
    {
        lock (_sync)
            _entries.Clear();
    }

    public IReadOnlyList<PlannerLogEntry> GetRecent(int maxCount)
    {
        if (maxCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCount), "maxCount 必须大于 0。");

        lock (_sync)
        {
            int skip = Math.Max(0, _entries.Count - maxCount);
            return _entries.Skip(skip).Select(CloneEntry).ToArray();
        }
    }

    public void Log(
        PlannerLogLevel level,
        string eventName,
        string message,
        string detail = "",
        string boxNumber = "")
    {
        if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(message))
            return;

        PlannerLogOptions optionsSnapshot;
        lock (_sync)
            optionsSnapshot = _options;

        if (!optionsSnapshot.Enabled || level < optionsSnapshot.MinLevel)
            return;

        var entry = new PlannerLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Level = level,
            EventName = eventName,
            Message = message,
            Detail = detail,
            BoxNumber = boxNumber,
        };

        lock (_sync)
        {
            _entries.Add(entry);
            TrimEntriesUnsafe(_options.MaxBufferedEntries);
        }

        if (!string.IsNullOrWhiteSpace(optionsSnapshot.FilePath))
            TryAppendToFile(optionsSnapshot.FilePath, entry);
    }

    private static PlannerLogOptions NormalizeOptions(PlannerLogOptions? options)
    {
        var normalized = options ?? new PlannerLogOptions();
        int maxBufferedEntries = normalized.MaxBufferedEntries <= 0
            ? DefaultMaxBufferedEntries
            : normalized.MaxBufferedEntries;

        return new PlannerLogOptions
        {
            Enabled = normalized.Enabled,
            MinLevel = normalized.MinLevel,
            MaxBufferedEntries = maxBufferedEntries,
            FilePath = string.IsNullOrWhiteSpace(normalized.FilePath)
                ? string.Empty
                : normalized.FilePath.Trim(),
        };
    }

    private static PlannerLogOptions CloneOptions(PlannerLogOptions options)
        => new()
        {
            Enabled = options.Enabled,
            MinLevel = options.MinLevel,
            MaxBufferedEntries = options.MaxBufferedEntries,
            FilePath = options.FilePath,
        };

    private static PlannerLogEntry CloneEntry(PlannerLogEntry entry)
        => new()
        {
            TimestampUtc = entry.TimestampUtc,
            Level = entry.Level,
            EventName = entry.EventName,
            Message = entry.Message,
            Detail = entry.Detail,
            BoxNumber = entry.BoxNumber,
        };

    private void TrimEntriesUnsafe(int maxBufferedEntries)
    {
        int overflow = _entries.Count - maxBufferedEntries;
        if (overflow > 0)
            _entries.RemoveRange(0, overflow);
    }

    private static void TryAppendToFile(string filePath, PlannerLogEntry entry)
    {
        try
        {
            string fullPath = Path.GetFullPath(filePath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.AppendAllText(fullPath, FormatEntry(entry) + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // 日志写盘失败不能影响主流程。
        }
    }

    private static string FormatEntry(PlannerLogEntry entry)
    {
        string timestamp = entry.TimestampUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        string detail = string.IsNullOrWhiteSpace(entry.Detail)
            ? ""
            : $" detail={entry.Detail.Replace(Environment.NewLine, " ")}";
        string box = string.IsNullOrWhiteSpace(entry.BoxNumber)
            ? ""
            : $" box={entry.BoxNumber}";
        return $"{timestamp} [{entry.Level}] {entry.EventName} - {entry.Message}{box}{detail}";
    }
}
