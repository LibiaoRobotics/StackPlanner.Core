namespace StackPlanner.Core;

/// <summary>
/// Core 内部维护的待堆垛库存数据库（内存实现）。
/// 主键为箱号，写入时采用 upsert 语义。
/// </summary>
internal sealed class InventoryDatabase
{
    private readonly Dictionary<string, InventoryBoxRecord> _records = new(StringComparer.Ordinal);

    /// <summary>读取库存记录副本。</summary>
    public IReadOnlyList<InventoryBoxRecord> GetAll()
        => _records.Values.Select(CloneRecord).ToArray();

    /// <summary>清空库存。</summary>
    public void Clear() => _records.Clear();

    /// <summary>
    /// 批量写入库存数据。
    /// replaceAll=true 时先清空库存，再写入本次数据。
    /// </summary>
    public InventoryUpsertResult Upsert(IEnumerable<InventoryBoxRecord> records, bool replaceAll)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (replaceAll)
            _records.Clear();

        int inserted = 0;
        int updated = 0;
        int invalid = 0;

        foreach (var item in records)
        {
            if (!TryNormalize(item, out var normalized))
            {
                invalid++;
                continue;
            }

            if (_records.ContainsKey(normalized.BoxNumber))
                updated++;
            else
                inserted++;

            _records[normalized.BoxNumber] = normalized;
        }

        int upserted = inserted + updated;
        return new InventoryUpsertResult
        {
            Upserted = upserted,
            Inserted = inserted,
            Updated = updated,
            Invalid = invalid,
            Total = _records.Count,
            Message = $"库存写入完成：新增 {inserted}，更新 {updated}，无效 {invalid}，当前总数 {_records.Count}。",
        };
    }

    private static bool TryNormalize(InventoryBoxRecord? record, out InventoryBoxRecord normalized)
    {
        normalized = default!;
        if (record is null)
            return false;

        string boxNumber = record.BoxNumber?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(boxNumber))
            return false;

        if (!double.IsFinite(record.LengthMm) || !double.IsFinite(record.HeightMm) || !double.IsFinite(record.DepthMm))
            return false;

        if (!double.IsFinite(record.WeightKg))
            return false;

        normalized = record with
        {
            BoxNumber = boxNumber,
            Source = record.Source?.Trim() ?? string.Empty,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        return true;
    }

    private static InventoryBoxRecord CloneRecord(InventoryBoxRecord input)
        => input with
        {
            Source = input.Source,
        };
}
