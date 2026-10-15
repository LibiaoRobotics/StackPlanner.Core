namespace StackPlanner.Core;

/// <summary>
/// 纯堆垛集合和确定性规划器。DLL 内部维护集合、顺序、当前方案和成功箱真实占用。
/// </summary>
public sealed class StackPlanner
{
    /// <summary>托盘可用区域的 X 最小值，单位为 mm。</summary>
    public const double PalletXMinMm = 1300;
    /// <summary>托盘可用区域的 X 最大值，单位为 mm。</summary>
    public const double PalletXMaxMm = 2400;
    /// <summary>托盘可用区域的 Y 最小值，单位为 mm。</summary>
    public const double PalletYMinMm = -1000;
    /// <summary>托盘可用区域的 Y 最大值，单位为 mm。</summary>
    public const double PalletYMaxMm = 100;
    /// <summary>相邻箱子之间的规划间隙，单位为 mm。</summary>
    public const double StackBoxGapMm = 20;
    /// <summary>托盘允许的最大堆垛高度，单位为 mm。</summary>
    public const double StackMaxHeightMm = 1500;
    /// <summary>托盘底面对应的机械 Z 基准值，单位为 mm。</summary>
    public const double PlaceFloorZMm = 1800;
    /// <summary>托盘边缘预留距离，单位为 mm。</summary>
    public const double EdgeMarginMm = 0;

    /// <summary>几何比较使用的固定误差，避免浮点边界导致结果不稳定。</summary>
    private const double Epsilon = 0.0001;
    /// <summary>有限宽度搜索每一轮最多保留的布局数量。</summary>
    private const int BeamWidth = 96;
    /// <summary>单个箱子最多保留的候选位置数量。</summary>
    private const int MaxCandidatesPerBox = 64;
    private const string UnrecognizedTypeId = "BT-UNRECOGNIZED";
    private const string InvalidTypeId = "BT-INVALID";
    private const double DefaultMaxSelectableWeightKg = 10d;
    private double _palletXMinMm;
    private double _palletXMaxMm;
    private double _palletYMinMm;
    private double _palletYMaxMm;
    /// <summary>DLL 当前维护的全部箱子。</summary>
    private readonly BoxGroup _group = new();
    /// <summary>已实际堆垛成功箱子的固定位置，不允许普通重规划改变。</summary>
    private readonly Dictionary<string, FixedPlacement> _succeeded = new(StringComparer.Ordinal);
    /// <summary>最近一次完整规划，用于恢复非 OnShelf 箱子的规划占用。</summary>
    private readonly Dictionary<string, BoxPlacement> _lastPlacements = new(StringComparer.Ordinal);
    /// <summary>已删除但等待同 TypeId 箱子补位的次数，仅供利用率规划使用。</summary>
    private readonly Dictionary<string, int> _pendingReplacementCounts = new(StringComparer.Ordinal);
    /// <summary>匹配删除记录、等待在利用率规划中追加到同 TypeId 末尾的新箱号。</summary>
    private readonly Dictionary<string, HashSet<string>> _pendingReplacementBoxes = new(StringComparer.Ordinal);
    /// <summary>删除后保留的、可供同 TypeId 新箱复用的规划位置。</summary>
    private readonly Dictionary<string, List<BoxPlacement>> _reusableUtilizationPlacements = new(StringComparer.Ordinal);
    /// <summary>记录可复用位置所属 TypeId 的尺寸，用于对外输出兼容维度信息。</summary>
    private readonly Dictionary<string, BoxDimension> _reusablePlacementTypeDimensions = new(StringComparer.Ordinal);
    /// <summary>待堆垛库存数据库（与当前规划箱集合解耦）。</summary>
    private readonly InventoryDatabase _inventoryDatabase = new();
    /// <summary>对外返回的最近一次规划结果。</summary>
    private StackPlanResult _lastPlan = new() { Placements = Array.Empty<BoxPlacement>(), PlanningResult = true };
    /// <summary>Core 内部日志器。</summary>
    private readonly StackPlannerLogger _logger = new();

    /// <summary>
    /// 使用默认托盘边界创建规划器。
    /// 托盘边界在实例创建后不可修改，保证一次任务内规划结果的坐标系稳定。
    /// </summary>
    public StackPlanner()
        : this(PalletXMinMm, PalletXMaxMm, PalletYMinMm, PalletYMaxMm)
    {
    }

    /// <summary>
    /// 使用指定托盘边界创建规划器。边界单位为 mm，且必须在首次规划前确定。
    /// </summary>
    public StackPlanner(
        double palletXMinMm,
        double palletXMaxMm,
        double palletYMinMm,
        double palletYMaxMm)
    {
        ValidatePalletDimensions(palletXMinMm, palletXMaxMm, palletYMinMm, palletYMaxMm);

        _palletXMinMm = palletXMinMm;
        _palletXMaxMm = palletXMaxMm;
        _palletYMinMm = palletYMinMm;
        _palletYMaxMm = palletYMaxMm;

        _logger.Log(
            PlannerLogLevel.Info,
            "planner.created",
            "已创建新的堆垛规划器实例。",
            detail: $"PalletX=[{_palletXMinMm},{_palletXMaxMm}], PalletY=[{_palletYMinMm},{_palletYMaxMm}]");
    }

    private static void ValidatePalletDimensions(
        double palletXMinMm,
        double palletXMaxMm,
        double palletYMinMm,
        double palletYMaxMm)
    {
        if (!double.IsFinite(palletXMinMm) || !double.IsFinite(palletXMaxMm)
            || !double.IsFinite(palletYMinMm) || !double.IsFinite(palletYMaxMm)
            || palletXMaxMm <= palletXMinMm || palletYMaxMm <= palletYMinMm)
            throw new ArgumentException("托盘边界必须为有限数值，且最大值必须大于最小值。");
    }

    /// <summary>更新日志配置。</summary>
    public void ConfigureLogging(PlannerLogOptions? options) => _logger.Configure(options);

    /// <summary>获取最近日志（按时间升序）。</summary>
    public IReadOnlyList<PlannerLogEntry> GetRecentLogs(int maxCount = 200) => _logger.GetRecent(maxCount);

    /// <summary>
    /// 获取当前箱子集合的副本。调用方修改返回对象不会影响 DLL 内部状态。
    /// </summary>
    public IReadOnlyList<Box> GetBoxes() => _group.Boxes.Select(CloneBox).ToArray();

    /// <summary>
    /// 批量写入待堆垛库存。
    /// replaceAll=true 时先清空库存，再写入本次数据。
    /// </summary>
    public InventoryUpsertResult UpsertInventoryBoxes(
        IEnumerable<InventoryBoxRecord> boxes,
        bool replaceAll = false)
    {
        try
        {
            var result = _inventoryDatabase.Upsert(boxes, replaceAll);
            _logger.Log(
                PlannerLogLevel.Info,
                "inventory.upsert",
                "库存写入完成。",
                detail: $"ReplaceAll={replaceAll}, Upserted={result.Upserted}, Inserted={result.Inserted}, Updated={result.Updated}, Invalid={result.Invalid}, Total={result.Total}");
            return result;
        }
        catch (Exception ex)
        {
            _logger.Log(
                PlannerLogLevel.Error,
                "inventory.upsert.failed",
                "库存写入失败。",
                detail: ex.Message);
            throw;
        }
    }

    /// <summary>
    /// 批量写入已成功堆垛数据。
    /// replaceAll=true 时先清空当前成功堆垛集合，再写入本次数据。
    /// 已存在成功箱的 Order 视为锁定值，不允许通过增量写入修改。
    /// </summary>
    public SucceededPlacementUpsertResult UpsertSucceededPlacements(
        IEnumerable<SucceededPlacementRecord> records,
        bool replaceAll = false)
    {
        ArgumentNullException.ThrowIfNull(records);

        var request = records.ToArray();
        _logger.Log(
            PlannerLogLevel.Info,
            "succeeded.upsert.begin",
            "开始批量写入成功堆垛数据。",
            detail: $"RequestCount={request.Length}, ReplaceAll={replaceAll}");

        int invalid = 0;
        var valid = new List<SucceededPlacementRecord>(request.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in request)
        {
            if (!TryNormalizeSucceededPlacement(item, out var normalized))
            {
                invalid++;
                continue;
            }

            if (!seen.Add(normalized.BoxNumber))
            {
                invalid++;
                continue;
            }

            if (!replaceAll
                && _succeeded.TryGetValue(normalized.BoxNumber, out var existing)
                && existing.Placement.Order != normalized.Placement.Order)
            {
                invalid++;
                continue;
            }

            valid.Add(normalized);
        }

        var finalSucceeded = new Dictionary<string, SucceededPlacementRecord>(StringComparer.Ordinal);
        if (!replaceAll)
        {
            foreach (var pair in _succeeded)
            {
                var box = FindBox(pair.Key);
                finalSucceeded[pair.Key] = new SucceededPlacementRecord
                {
                    BoxNumber = pair.Key,
                    LengthMm = box?.LengthMm ?? pair.Value.Length,
                    WidthMm = box?.WidthMm ?? pair.Value.Width,
                    HeightMm = box?.HeightMm ?? pair.Value.Height,
                    WeightKg = box?.WeightKg ?? 0,
                    Placement = ClonePlacement(pair.Value.Placement),
                };
            }
        }

        foreach (var item in valid)
            finalSucceeded[item.BoxNumber] = item;

        if (!ValidateSucceededSet(finalSucceeded.Values, out string validationMessage))
        {
            _logger.Log(
                PlannerLogLevel.Warning,
                "succeeded.upsert.rejected",
                "成功堆垛集合校验未通过，已拒绝本次写入。",
                detail: validationMessage);
            return new SucceededPlacementUpsertResult
            {
                Upserted = 0,
                Inserted = 0,
                Updated = 0,
                Invalid = invalid + valid.Count,
                TotalSucceeded = _succeeded.Count,
                Message = validationMessage,
            };
        }

        var existedSucceeded = _succeeded.Keys.ToHashSet(StringComparer.Ordinal);
        int inserted = 0;
        int updated = 0;
        foreach (var item in valid)
        {
            if (existedSucceeded.Contains(item.BoxNumber))
                updated++;
            else
                inserted++;
        }

        if (replaceAll)
        {
            var keep = finalSucceeded.Keys.ToHashSet(StringComparer.Ordinal);
            for (int index = 0; index < _group.MutableBoxes.Count; index++)
            {
                var box = _group.MutableBoxes[index];
                if (box.Status != BoxStatus.StackingSucceeded || keep.Contains(box.BoxNumber))
                    continue;

                _group.MutableBoxes[index] = box with
                {
                    Status = BoxStatus.OnShelf,
                    Order = -1,
                };
                _lastPlacements.Remove(box.BoxNumber);
            }

            _succeeded.Clear();
        }

        foreach (var item in finalSucceeded.Values)
        {
            int index = _group.MutableBoxes.FindIndex(x => string.Equals(x.BoxNumber, item.BoxNumber, StringComparison.Ordinal));
            if (index >= 0)
            {
                _group.MutableBoxes[index] = _group.MutableBoxes[index] with
                {
                    LengthMm = item.LengthMm,
                    WidthMm = item.WidthMm,
                    HeightMm = item.HeightMm,
                    WeightKg = item.WeightKg,
                    Status = BoxStatus.StackingSucceeded,
                    Order = item.Placement.Order,
                };
            }
            else
            {
                _group.MutableBoxes.Add(new Box
                {
                    BoxNumber = item.BoxNumber,
                    LengthMm = item.LengthMm,
                    WidthMm = item.WidthMm,
                    HeightMm = item.HeightMm,
                    WeightKg = item.WeightKg,
                    Status = BoxStatus.StackingSucceeded,
                    Order = item.Placement.Order,
                });
            }

            var placement = ClonePlacement(item.Placement);
            _lastPlacements[item.BoxNumber] = placement;
            _succeeded[item.BoxNumber] = new FixedPlacement(
                placement,
                item.LengthMm,
                item.WidthMm,
                item.HeightMm);
        }

        var existingNumbers = _group.MutableBoxes
            .Select(x => x.BoxNumber)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var pair in _lastPlacements.Keys.ToArray())
        {
            if (!existingNumbers.Contains(pair))
                _lastPlacements.Remove(pair);
        }

        var planPlacements = _lastPlacements.Values
            .Where(item => existingNumbers.Contains(item.BoxNumber))
            .OrderBy(item => item.Order)
            .ThenBy(item => item.BoxNumber, StringComparer.Ordinal)
            .Select(ClonePlacement)
            .ToArray();
        _lastPlan = new StackPlanResult
        {
            Placements = planPlacements,
            PlanningResult = planPlacements.Length == _group.MutableBoxes.Count,
        };

        int upserted = inserted + updated;
        var result = new SucceededPlacementUpsertResult
        {
            Upserted = upserted,
            Inserted = inserted,
            Updated = updated,
            Invalid = invalid,
            TotalSucceeded = _succeeded.Count,
            Message = $"成功堆垛写入完成：新增 {inserted}，更新 {updated}，无效 {invalid}，当前成功总数 {_succeeded.Count}。" + validationMessage,
        };

        _logger.Log(
            PlannerLogLevel.Info,
            "succeeded.upsert.completed",
            "成功堆垛数据写入完成。",
            detail: $"ReplaceAll={replaceAll}, Upserted={result.Upserted}, Inserted={result.Inserted}, Updated={result.Updated}, Invalid={result.Invalid}, TotalSucceeded={result.TotalSucceeded}");
        return result;
    }

    /// <summary>
    /// 对库存箱子进行箱型分类。
    /// 分类定义来自本地 stackplanner.config，尺寸比较固定使用 ±30mm。
    /// </summary>
    public BoxTypeClassificationResult ClassifyInventoryBoxTypes(double toleranceMm = 30)
        => BoxTypeClassifier.Classify(_inventoryDatabase.GetAll(), toleranceMm);

    /// <summary>
    /// 获取 Core 配置中的全部箱型定义（来自 stackplanner.config），界面以此作为最佳策略箱型的唯一来源。
    /// </summary>
    public IReadOnlyList<BoxTypeDefinition> GetBoxTypeDefinitions()
        => BoxTypeClassifier.GetDefinitions();

    /// <summary>获取最近一次规划结果的副本。</summary>
    public StackPlanResult CurrentPlan => ClonePlan(_lastPlan);

    /// <summary>
    /// 获取逐箱明细视图，包含堆垛顺序、类别、尺寸、重量、状态和规划字段。
    /// 所有字段均由 Core 输出，供 DLL 消费方直接使用。
    /// </summary>
    public PlannerBoxDetailResult GetPlannerBoxDetails(double toleranceMm = 30)
    {
        if (!double.IsFinite(toleranceMm) || toleranceMm < 0)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm), "容差参数必须是大于等于 0 的有限数值。");

        var placementsByNumber = _lastPlan.Placements
            .GroupBy(item => item.BoxNumber, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.Order).First(),
                StringComparer.Ordinal);

        var details = _group.Boxes
            .Select(box =>
            {
                placementsByNumber.TryGetValue(box.BoxNumber, out var placement);
                string typeId = BoxTypeClassifier.ClassifyPlanBoxTypeId(
                    box.LengthMm,
                    box.WidthMm,
                    box.HeightMm,
                    toleranceMm);

                return new PlannerBoxDetail
                {
                    Order = placement?.Order ?? box.Order,
                    BoxNumber = box.BoxNumber,
                    TypeId = typeId,
                    LengthMm = box.LengthMm,
                    WidthMm = box.WidthMm,
                    HeightMm = box.HeightMm,
                    WeightKg = box.WeightKg,
                    Status = box.Status,
                    Planned = placement is not null,
                    Xmm = placement?.Xmm,
                    Ymm = placement?.Ymm,
                    Zmm = placement?.Zmm,
                    OrientationDeg = placement?.OrientationDeg,
                    LayerIndex = placement?.LayerIndex,
                };
            })
            .OrderBy(item => item.Order)
            .ThenBy(item => item.BoxNumber, StringComparer.Ordinal)
            .ToArray();

        return new PlannerBoxDetailResult
        {
            ToleranceMm = toleranceMm,
            PlanningResult = _lastPlan.PlanningResult,
            TotalBoxes = _group.Boxes.Count,
            PlannedBoxes = placementsByNumber.Count,
            Boxes = details,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// 从完整快照恢复 Core 状态。恢复前会校验箱号、箱子属性和规划引用关系。
    /// </summary>
    public void RestoreSnapshot(PlannerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != 1)
            throw new InvalidDataException($"不支持的规划快照版本：{snapshot.SchemaVersion}");

        var boxes = snapshot.Boxes.Select(CloneBox).ToArray();
        var numbers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var box in boxes)
        {
            ValidateBox(box);
            if (!numbers.Add(box.BoxNumber))
                throw new InvalidDataException($"规划快照包含重复箱号：{box.BoxNumber}");
        }

        var placements = snapshot.Plan.Placements.Select(ClonePlacement).ToArray();
        if (placements.Any(x => !numbers.Contains(x.BoxNumber))
            || placements.Select(x => x.BoxNumber).Distinct(StringComparer.Ordinal).Count() != placements.Length)
            throw new InvalidDataException("规划快照中的放置结果包含未知或重复箱号。");

        _group.MutableBoxes.Clear();
        _group.MutableBoxes.AddRange(boxes);
        _succeeded.Clear();
        _lastPlacements.Clear();
        _pendingReplacementCounts.Clear();
        _pendingReplacementBoxes.Clear();
        _reusableUtilizationPlacements.Clear();
        _reusablePlacementTypeDimensions.Clear();

        foreach (var placement in placements)
            _lastPlacements[placement.BoxNumber] = placement;
        foreach (var box in boxes.Where(x => x.Status == BoxStatus.StackingSucceeded))
        {
            if (!_lastPlacements.TryGetValue(box.BoxNumber, out var placement))
                throw new InvalidDataException($"堆垛成功箱子缺少固定位置：{box.BoxNumber}");
            _succeeded[box.BoxNumber] = new FixedPlacement(
                ClonePlacement(placement), box.LengthMm, box.WidthMm, box.HeightMm);
        }
        foreach (var reusableSet in snapshot.ReusablePlacements)
        {
            string key = SameSpecTypeId(
                reusableSet.Dimension.LengthMm,
                reusableSet.Dimension.WidthMm,
                reusableSet.Dimension.HeightMm);
            _reusableUtilizationPlacements[key] = reusableSet.Placements
                .Select(ClonePlacement)
                .ToList();
            _reusablePlacementTypeDimensions[key] = new BoxDimension
            {
                LengthMm = reusableSet.Dimension.LengthMm,
                WidthMm = reusableSet.Dimension.WidthMm,
                HeightMm = reusableSet.Dimension.HeightMm,
            };
        }

        _lastPlan = new StackPlanResult
        {
            Placements = placements,
            PlanningResult = snapshot.Plan.PlanningResult,
        };
    }

    /// <summary>
    /// 不重新规划地增加一个箱子。
    /// 新箱子只能使用此前减箱释放的同 TypeId 规划位置；没有可复用位置时不会增加箱子。
    /// </summary>
    /// <param name="box">待增加的箱子，必须处于 OnShelf 状态。</param>
    /// <returns>增加结果，以及操作后的完整箱子集合和规划快照。</returns>
    public UtilizationMutationResult AddBoxWithoutReplanning(Box box)
    {
        ArgumentNullException.ThrowIfNull(box);
        ValidateBox(box);

        if (box.Status != BoxStatus.OnShelf)
            return MutationFailure("新增箱子必须处于 OnShelf 状态。");
        if (_group.MutableBoxes.Any(x => string.Equals(x.BoxNumber, box.BoxNumber, StringComparison.Ordinal)))
            return MutationFailure($"箱号已存在：{box.BoxNumber}。");
        if (!_lastPlan.PlanningResult || _lastPlacements.Count != _group.MutableBoxes.Count)
            return MutationFailure("当前没有完整有效的利用率规划，不能不重新规划地加箱。");

        string key = SameSpecTypeId(box);
        if (!_reusableUtilizationPlacements.TryGetValue(key, out var reusable)
            || reusable.Count == 0)
            return MutationFailure($"箱型 {key} 没有可复用的规划位置，不能不重新规划地加箱。");

        var added = CloneBox(box);
        var source = reusable.MinBy(point => point.Order)!;
        reusable.Remove(source);
        _group.MutableBoxes.Add(added);

        var placement = source with
        {
            BoxNumber = added.BoxNumber,
        };
        added.Order = placement.Order;
        _lastPlacements[added.BoxNumber] = placement;
        _lastPlan = new StackPlanResult
        {
            Placements = _lastPlan.Placements
                .Append(placement)
                .OrderBy(point => point.Order)
                .ToArray(),
            PlanningResult = true,
        };
        return MutationSuccess("不重新规划地加箱完成。");
    }

    /// <summary>
    /// 不重新规划地删除一个箱子。
    /// 删除后，同 TypeId 箱子依次使用前一个箱子的规划位置，并保留最后释放的位置供加箱复用。
    /// </summary>
    /// <param name="box">待删除的箱子；仅使用其 BoxNumber 定位 Core 内部箱子。</param>
    /// <returns>删除结果，以及操作后的完整箱子集合和规划快照。</returns>
    public UtilizationMutationResult RemoveAndAddSameSpecBox(Box box)
    {
        ArgumentNullException.ThrowIfNull(box);
        var existing = FindBox(box.BoxNumber);
        if (existing is null)
            return MutationFailure($"箱号不存在：{box.BoxNumber}。");
        if (existing.Status is BoxStatus.Stacking or BoxStatus.StackingSucceeded)
            return MutationFailure("堆垛中或堆垛成功的箱子不能删除。");
        if (!_lastPlan.PlanningResult || _lastPlacements.Count != _group.MutableBoxes.Count)
            return MutationFailure("当前没有完整有效的利用率规划，不能不重新规划地减箱。");

        var removed = CloneBox(existing);
        RemoveUtilizationBox(existing.BoxNumber);

        var replacement = FindInventoryReplacementBox(removed);
        if (replacement is null)
            return MutationSuccess("不重新规划地减箱完成。库存中未找到同 TypeId 可补箱子，已跳过自动补箱。");

        var addResult = AddBoxWithoutReplanning(replacement);
        if (!addResult.Succeeded)
            return MutationFailure($"不重新规划地减箱完成，但自动补箱失败：{addResult.Message}");

        return MutationSuccess($"不重新规划地减箱并自动补箱完成：{replacement.BoxNumber}。");
    }

    private Box? FindInventoryReplacementBox(Box removed)
    {
        string removedTypeId = SameSpecTypeId(removed);
        var excludedBoxNumbers = _group.MutableBoxes
            .Select(x => x.BoxNumber)
            .Append(removed.BoxNumber)
            .ToHashSet(StringComparer.Ordinal);

        var replacement = _inventoryDatabase
            .GetAll()
            .Where(record => !excludedBoxNumbers.Contains(record.BoxNumber))
            .Where(record => string.Equals(SameSpecTypeId(record), removedTypeId, StringComparison.Ordinal))
            .OrderBy(record => record.BoxNumber, StringComparer.Ordinal)
            .FirstOrDefault();

        if (replacement is null)
            return null;

        return new Box
        {
            BoxNumber = replacement.BoxNumber,
            LengthMm = replacement.DepthMm,
            WidthMm = replacement.LengthMm,
            HeightMm = replacement.HeightMm,
            WeightKg = replacement.WeightKg,
            Status = BoxStatus.OnShelf,
        };
    }

    /// <summary>
    /// 更新箱子的业务状态，并在状态变为 StackingSucceeded 时固化其当前规划位置。
    /// 该方法只记录业务动作结果，不执行机械动作。
    /// </summary>
    /// <param name="boxNumber">待更新的唯一箱号。</param>
    /// <param name="status">新的业务状态。</param>
    /// <param name="actualPlacement">
    /// 箱子实际堆垛成功时的放置结果；为空时使用规划器当前记录的位置。
    /// </param>
    /// <returns>箱号存在并完成更新返回 true，否则返回 false。</returns>
    public bool UpdateBoxStatus(string boxNumber, BoxStatus status, BoxPlacement? actualPlacement = null)
    {
        var box = FindBox(boxNumber);
        if (box is null)
            return false;
        if (actualPlacement is not null)
            SetInternalPlacement(boxNumber, actualPlacement);
        box.Status = status;
        if (status == BoxStatus.StackingSucceeded)
        {
            if (!_lastPlacements.TryGetValue(boxNumber, out var placement))
                throw new InvalidOperationException($"箱子 {boxNumber} 没有可确认的规划位置。");
            _succeeded[boxNumber] = new FixedPlacement(ClonePlacement(placement), box.LengthMm, box.WidthMm, box.HeightMm);
        }
        else
        {
            _succeeded.Remove(boxNumber);
        }
        return true;
    }

    /// <summary>
    /// 在核心内部写入箱子已经实际占用的放置位置。
    /// 外部只通过 <see cref="UpdateBoxStatus(string, BoxStatus, BoxPlacement?)" />
    /// 报告业务状态和成功位置，不直接调用此方法。
    /// </summary>
    /// <param name="boxNumber">箱子的唯一编号。</param>
    /// <param name="placement">箱子已经实际使用的放置位置。</param>
    /// <returns>箱子存在且位置合法时返回 true。</returns>
    private bool SetInternalPlacement(string boxNumber, BoxPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var box = FindBox(boxNumber);
        if (box is null || !string.Equals(boxNumber, placement.BoxNumber, StringComparison.Ordinal))
            return false;
        if (placement.Order < 0 || placement.LayerIndex < 0
            || placement.OrientationDeg is not (0 or 90))
            throw new ArgumentException("箱子放置位置的顺序、层号或朝向不合法。", nameof(placement));

        var definitionsByTypeId = GetBoxTypeDefinitions()
            .ToDictionary(item => item.TypeId, item => item, StringComparer.Ordinal);
        var collisionBox = ToCollisionSizedBox(box, definitionsByTypeId);
        var placed = ToPlacedBox(collisionBox, placement, real: true);
        if (!InsidePallet(placed))
            throw new ArgumentException($"箱子 {boxNumber} 的固定位置超出托盘范围。", nameof(placement));

        box.Order = placement.Order;
        _lastPlacements[boxNumber] = ClonePlacement(placement);
        if (box.Status == BoxStatus.StackingSucceeded)
            _succeeded[boxNumber] = new FixedPlacement(ClonePlacement(placement), box.LengthMm, box.WidthMm, box.HeightMm);
        return true;
    }

    /// <summary>
    /// 根据候选尺寸生成逐层空间利用率规划，并读取库存中各箱型的可选数量作为上限。
    /// 每个箱型的上限 = 库存中同类别、重量在 (0, maxSelectableWeightKg] 内的箱子数量；
    /// 库存中没有该类箱子时上限为 0，不参与规划。结果同时返回箱子数量和空间利用率。
    /// </summary>
    /// <param name="dimensions">候选箱型尺寸与最小数量。</param>
    /// <param name="maxSelectableWeightKg">库存选箱允许的最大重量（kg），默认 10kg。</param>
    /// <exception cref="ArgumentOutOfRangeException">maxSelectableWeightKg 不是有限正数。</exception>
    public UtilizationPlanResult PlanBestUtilization(
        IEnumerable<BoxDimension> dimensions,
        double maxSelectableWeightKg = DefaultMaxSelectableWeightKg)
        => PlanBestUtilizationCore(dimensions, maxSelectableWeightKg, excludedInventoryBoxNumbers: null);

    private UtilizationPlanResult PlanBestUtilizationCore(
        IEnumerable<BoxDimension> dimensions,
        double maxSelectableWeightKg,
        IReadOnlySet<string>? excludedInventoryBoxNumbers,
        double maxStackHeightMm = StackMaxHeightMm)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        if (!double.IsFinite(maxSelectableWeightKg) || maxSelectableWeightKg <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSelectableWeightKg), "最大选箱重量必须是有限正数。");
        var input = dimensions.ToArray();
        if (input.Length == 0)
            return EmptyUtilizationPlan("至少需要一个箱子尺寸。");

        foreach (var dimension in input)
        {
            if (dimension is null || !double.IsFinite(dimension.LengthMm)
                || !double.IsFinite(dimension.WidthMm) || !double.IsFinite(dimension.HeightMm)
                || dimension.LengthMm <= 0 || dimension.WidthMm <= 0 || dimension.HeightMm <= 0
                || dimension.MinimumCount < 0)
                return EmptyUtilizationPlan("箱子尺寸必须为有限正数，最小数量不能小于 0。");
        }

        var unique = input
            .GroupBy(x => DimensionKey(x), StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return first with { MinimumCount = group.Max(x => x.MinimumCount) };
            })
            .OrderBy(x => DimensionKey(x), StringComparer.Ordinal)
            .ToArray();

        // 库存上限：每个候选箱型可用的真实库存数量（与选箱使用同一套筛选条件）。
        var selectableCounts = CountSelectableInventoryByType(maxSelectableWeightKg, excludedInventoryBoxNumbers);
        var typeIdByShape = GetBoxTypeDefinitions()
            .GroupBy(item => ExactDimensionKey(item.RepLengthMm, item.RepDepthMm, item.RepHeightMm), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.TypeId, StringComparer.Ordinal).First().TypeId,
                StringComparer.Ordinal);
        var caps = unique.ToDictionary(
            x => DimensionKey(x),
            x => typeIdByShape.TryGetValue(ExactDimensionKey(x.LengthMm, x.WidthMm, x.HeightMm), out var typeId)
                ? selectableCounts.GetValueOrDefault(typeId)
                : 0,
            StringComparer.Ordinal);

        // 最小数量超过库存上限时直接失败，避免被误报为层堆叠无解。
        var shortages = unique
            .Where(x => x.MinimumCount > caps[DimensionKey(x)])
            .Select(x => $"{x.LengthMm:0}×{x.WidthMm:0}×{x.HeightMm:0}mm 需要 {x.MinimumCount} 个，库存可选 {caps[DimensionKey(x)]} 个")
            .ToArray();
        if (shortages.Length > 0)
            return EmptyUtilizationPlan("库存可选箱子不足：" + string.Join("；", shortages) + "。", false);

        // 上限为 0 的箱型（库存中没有可选箱子）不参与规划。
        var available = unique.Where(x => caps[DimensionKey(x)] > 0).ToArray();
        if (available.Length == 0)
            return EmptyUtilizationPlan("库存中没有可用于任何候选尺寸的可选箱子。", false);

        // 直接搜索真实二维布局，不再生成有限数量的临时箱子；每层与堆叠都受库存上限约束。
        var candidates = available
            .GroupBy(x => x.HeightMm)
            .SelectMany(group => TypeCombinations(
                group.OrderBy(x => DimensionKey(x), StringComparer.Ordinal).ToArray(), 3))
            .Select(types => FindBestDirectLayer(types, caps))
            .Where(x => x is not null)
            .Cast<DirectLayerResult>()
            .ToArray();
        if (candidates.Length == 0)
            return EmptyUtilizationPlan("没有尺寸可以形成合法布局。");

        var selected = FindBestLayerStack(candidates, available, caps, maxStackHeightMm);
        if (selected is null)
            return EmptyUtilizationPlan("在托盘尺寸和最大堆垛高度限制下，无法满足箱型最小数量约束。", false);

        var layers = new List<LayerUtilizationSummary>();
        var selectedCounts = selected.Layers
            .SelectMany(x => x.Placements)
            .GroupBy(x => DimensionKey(x.Dimension), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var plannedCounts = unique
            .Select(dimension => new BoxTypeCount
            {
                LengthMm = dimension.LengthMm,
                WidthMm = dimension.WidthMm,
                HeightMm = dimension.HeightMm,
                Count = selectedCounts.GetValueOrDefault(DimensionKey(dimension)),
            })
            .OrderBy(x => DimensionKey(new BoxDimension
            {
                LengthMm = x.LengthMm,
                WidthMm = x.WidthMm,
                HeightMm = x.HeightMm,
            }), StringComparer.Ordinal)
            .ToArray();
        double palletArea = (_palletXMaxMm - _palletXMinMm) * (_palletYMaxMm - _palletYMinMm);
        for (int layerIndex = 0; layerIndex < selected.Layers.Count; layerIndex++)
        {
            var layer = selected.Layers[layerIndex];
            layers.Add(new LayerUtilizationSummary
            {
                LayerIndex = layerIndex,
                Utilization = layer.OccupiedArea / palletArea,
            });
        }

        double utilization = layers.Count == 0 ? 0 : layers.Average(x => x.Utilization);
        return new UtilizationPlanResult
        {
            PlannedCounts = plannedCounts,
            Layers = layers,
            Utilization = utilization,
            PlanningResult = true,
        };
    }

    /// <summary>
    /// 基于库存的利用率规划：先按候选箱型计算最佳策略，由 Core 从库存挑选真实箱号写入当前内存，
    /// 再对选中的真实箱子执行利用率规划，规划结果保存在 Core 内存快照中。
    /// </summary>
    /// <param name="dimensions">
    /// 候选箱型尺寸与最小数量。
    /// 传入 null 或空集合时，优先使用 stackplanner.config 的 defaultUtilizationDimensions；
    /// 未配置该字段时回退到全部箱型尺寸（MinimumCount=0）。
    /// </param>
    /// <param name="maxSelectableWeightKg">库存选箱允许的最大重量（kg），默认 10kg。</param>
    public InventoryUtilizationPlanResult PlanUtilizationFromInventory(
        IEnumerable<BoxDimension>? dimensions = null,
        double maxSelectableWeightKg = DefaultMaxSelectableWeightKg)
    {
        var resolvedDimensions = ResolveInventoryUtilizationDimensions(dimensions);
        _logger.Log(
            PlannerLogLevel.Info,
            "utilization.inventory.begin",
            "开始执行基于库存的利用率规划。",
            detail: $"MaxSelectableWeightKg={maxSelectableWeightKg:R}");
        _logger.Log(
            PlannerLogLevel.Debug,
            "utilization.inventory.dimensions",
            "已解析本次利用率规划候选尺寸。",
            detail: $"CandidateCount={resolvedDimensions.Count}, Candidates={DescribeDimensions(resolvedDimensions)}");

        var selection = SelectInventoryBoxesForUtilization(
            resolvedDimensions,
            maxSelectableWeightKg);
        if (!selection.Applied)
        {
            _logger.Log(
                PlannerLogLevel.Warning,
                "utilization.inventory.selection_failed",
                "库存选箱阶段失败，未应用到 Core 内存。",
                detail: selection.Message);
            return selection;
        }

        _logger.Log(
            PlannerLogLevel.Debug,
            "utilization.inventory.selection_applied",
            "库存选箱阶段已成功应用到 Core 内存。",
            detail: $"SelectedBoxCount={selection.SelectedBoxCount}, PlannedCounts={DescribePlannedCounts(selection.PlannedCounts)}, LayerCount={selection.Layers.Count}, Utilization={selection.Utilization:R}");

        var plan = GenerateBestUtilizationPlan();
        if (!plan.PlanningResult)
        {
            _logger.Log(
                PlannerLogLevel.Warning,
                "utilization.inventory.plan_failed",
                "库存选箱后真实箱子利用率规划失败。",
                detail: $"Placements={plan.Placements.Count}, TotalBoxes={_group.MutableBoxes.Count}, OnShelf={_group.MutableBoxes.Count(x => x.Status == BoxStatus.OnShelf)}, Succeeded={_group.MutableBoxes.Count(x => x.Status == BoxStatus.StackingSucceeded)}, StatusSummary={DescribeStatusSummary(_group.MutableBoxes)}");
        }

        var result = new InventoryUtilizationPlanResult
        {
            PlanningResult = plan.PlanningResult,
            Message = plan.PlanningResult
                ? selection.Message
                : "库存选箱已写入 Core 内存，但真实箱子利用率规划失败。",
            PlannedCounts = selection.PlannedCounts,
            Layers = selection.Layers,
            Utilization = selection.Utilization,
            Applied = true,
            SelectedBoxCount = selection.SelectedBoxCount,
        };

        _logger.Log(
            result.PlanningResult ? PlannerLogLevel.Info : PlannerLogLevel.Warning,
            "utilization.inventory.completed",
            "基于库存的利用率规划完成。",
            detail: $"PlanningResult={result.PlanningResult}, Applied={result.Applied}, SelectedBoxCount={result.SelectedBoxCount}, Utilization={result.Utilization:R}");
        return result;
    }

    /// <summary>
    /// 解析库存利用率规划使用的候选尺寸：调用方传参优先；未传或空集合时回退到配置箱型。
    /// </summary>
    private IReadOnlyList<BoxDimension> ResolveInventoryUtilizationDimensions(
        IEnumerable<BoxDimension>? dimensions)
    {
        if (dimensions is not null)
        {
            var provided = dimensions.ToArray();
            if (provided.Length > 0)
                return provided;
        }

        var defaultsFromConfig = BoxTypeClassifier.GetDefaultUtilizationDimensions();
        if (defaultsFromConfig.Count > 0)
            return defaultsFromConfig;

        return GetBoxTypeDefinitions()
            .Select(definition => new BoxDimension
            {
                LengthMm = definition.RepLengthMm,
                WidthMm = definition.RepDepthMm,
                HeightMm = definition.RepHeightMm,
                MinimumCount = 0,
            })
            .ToArray();
    }

    /// <summary>
    /// 判断库存箱子是否可用于选箱：箱型有效，且重量在 (0, maxSelectableWeightKg] 内。
    /// </summary>
    private static bool IsSelectableInventoryAssignment(BoxTypeAssignment item, double maxSelectableWeightKg)
        => !string.IsNullOrWhiteSpace(item.TypeId)
           && !string.Equals(item.TypeId, UnrecognizedTypeId, StringComparison.Ordinal)
           && !string.Equals(item.TypeId, InvalidTypeId, StringComparison.Ordinal)
           && item.WeightKg > 0
           && item.WeightKg <= maxSelectableWeightKg;

    /// <summary>统计库存中各箱型的可选数量（与选箱使用同一筛选条件）。</summary>
    private Dictionary<string, int> CountSelectableInventoryByType(
        double maxSelectableWeightKg,
        IReadOnlySet<string>? excludedInventoryBoxNumbers = null)
        => ClassifyInventoryBoxTypes().Assignments
            .Where(item => IsSelectableInventoryAssignment(item, maxSelectableWeightKg))
            .Where(item => excludedInventoryBoxNumbers is null
                || !excludedInventoryBoxNumbers.Contains(item.BoxNumber))
            .GroupBy(item => item.TypeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private IReadOnlyList<BoxDimension> ReduceMinimumCountsBySucceeded(
        IEnumerable<BoxDimension> dimensions,
        IReadOnlyList<Box> succeededBoxes)
    {
        var requested = dimensions.ToArray();
        if (requested.Length == 0 || succeededBoxes.Count == 0)
            return requested;

        var candidateKeys = requested
            .Select(DimensionKey)
            .ToHashSet(StringComparer.Ordinal);
        if (candidateKeys.Count == 0)
            return requested;

        var definitionsByTypeId = GetBoxTypeDefinitions()
            .ToDictionary(item => item.TypeId, item => item, StringComparer.Ordinal);

        var succeededCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var box in succeededBoxes)
        {
            string typeId = BoxTypeClassifier.ClassifyPlanBoxTypeId(
                box.LengthMm,
                box.WidthMm,
                box.HeightMm);

            string key;
            if (definitionsByTypeId.TryGetValue(typeId, out var definition))
            {
                key = DimensionKey(new BoxDimension
                {
                    LengthMm = definition.RepLengthMm,
                    WidthMm = definition.RepDepthMm,
                    HeightMm = definition.RepHeightMm,
                });
            }
            else
            {
                key = DimensionKey(ToDimension(box));
            }

            if (!candidateKeys.Contains(key))
                continue;

            succeededCounts[key] = succeededCounts.GetValueOrDefault(key) + 1;
        }

        return requested
            .Select(dimension =>
            {
                string key = DimensionKey(dimension);
                int remainingMinimum = Math.Max(0,
                    dimension.MinimumCount - succeededCounts.GetValueOrDefault(key));
                return dimension with { MinimumCount = remainingMinimum };
            })
            .ToArray();
    }

    private bool TryBuildSucceededLayerFillPlan(
        SucceededBaselineState baseline,
        out SucceededLayerFillPlan? fillPlan,
        out string message)
    {
        fillPlan = null;
        message = string.Empty;
        if (baseline.Boxes.Count == 0)
            return true;

        var definitionsByTypeId = GetBoxTypeDefinitions()
            .ToDictionary(item => item.TypeId, item => item, StringComparer.Ordinal);
        var collisionBoxes = BuildCollisionBoxesByNumber(baseline.Boxes, definitionsByTypeId);
        var occupied = new List<PlacedBox>();
        foreach (var placement in baseline.Placements
                     .OrderBy(x => x.Order)
                     .ThenBy(x => x.BoxNumber, StringComparer.Ordinal))
        {
            if (!collisionBoxes.TryGetValue(placement.BoxNumber, out var collisionBox))
            {
                message = $"成功基线缺少箱子 {placement.BoxNumber} 的尺寸信息。";
                return false;
            }

            var placed = ToPlacedBox(collisionBox, placement, real: true);
            if (!Fits(placed, occupied))
            {
                message = $"成功基线中的箱子 {placement.BoxNumber} 占位冲突，无法继续利用率规划。";
                return false;
            }

            occupied.Add(placed);
        }

        int targetLayer = occupied.Max(x => x.Placement.LayerIndex);
        var targetLayerBoxes = occupied
            .Where(x => x.Placement.LayerIndex == targetLayer)
            .ToArray();
        if (targetLayerBoxes.Length == 0)
            return true;

        var typedLayerBoxes = new List<(PlacedBox Placed, Box Source, string TypeId)>(targetLayerBoxes.Length);
        foreach (var placed in targetLayerBoxes)
        {
            if (!collisionBoxes.TryGetValue(placed.Placement.BoxNumber, out var sourceBox))
            {
                message = $"成功层箱子 {placed.Placement.BoxNumber} 缺少尺寸信息，无法识别箱型。";
                return false;
            }

            string typeId = BoxTypeClassifier.ClassifyPlanBoxTypeId(
                sourceBox.LengthMm,
                sourceBox.WidthMm,
                sourceBox.HeightMm);
            typedLayerBoxes.Add((placed, sourceBox, typeId));
        }

        var succeededTypeSample = typedLayerBoxes
            .GroupBy(x => x.TypeId, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(x => x.Placed.Placement.Order)
                .ThenBy(x => x.Placed.Placement.BoxNumber, StringComparer.Ordinal)
                .First())
            .First();
        string succeededTypeId = succeededTypeSample.TypeId;

        var probeDimension = definitionsByTypeId.TryGetValue(succeededTypeId, out var definition)
            ? new BoxDimension
            {
                LengthMm = definition.RepLengthMm,
                WidthMm = definition.RepDepthMm,
                HeightMm = definition.RepHeightMm,
                MinimumCount = 0,
            }
            : new BoxDimension
            {
                LengthMm = succeededTypeSample.Source.LengthMm,
                WidthMm = succeededTypeSample.Source.WidthMm,
                HeightMm = succeededTypeSample.Source.HeightMm,
                MinimumCount = 0,
            };

        var sameTypeTemplate = new Box
        {
            BoxNumber = "__succeeded_layer_probe__",
            LengthMm = succeededTypeSample.Source.LengthMm,
            WidthMm = succeededTypeSample.Source.WidthMm,
            HeightMm = succeededTypeSample.Source.HeightMm,
            WeightKg = 0,
            Status = BoxStatus.OnShelf,
            Order = 0,
        };

        double targetBaseZ = succeededTypeSample.Placed.BaseZ;
        int neededCount = 0;
        while (true)
        {
            var candidate = GenerateCandidates(sameTypeTemplate, occupied)
                .FirstOrDefault(x => x.Placement.LayerIndex == targetLayer
                    && Math.Abs(x.Candidate.BaseZ - targetBaseZ) <= Epsilon);
            if (candidate is null)
                break;

            var placed = ToPlacedBox(sameTypeTemplate, candidate.Placement, real: false);
            if (!Fits(placed, occupied))
                break;

            occupied.Add(placed);
            neededCount++;
        }

        fillPlan = new SucceededLayerFillPlan(
            succeededTypeId,
            probeDimension,
            neededCount);
        return true;
    }

    private static IReadOnlyList<BoxDimension> ExcludeCandidateDimension(
        IReadOnlyList<BoxDimension> dimensions,
        BoxDimension excludedDimension)
    {
        string excludedKey = ExactDimensionKey(
            excludedDimension.LengthMm,
            excludedDimension.WidthMm,
            excludedDimension.HeightMm);

        return dimensions
            .Where(item => !string.Equals(
                ExactDimensionKey(item.LengthMm, item.WidthMm, item.HeightMm),
                excludedKey,
                StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>
    /// 按候选箱型计算最佳策略，并从库存挑选真实箱号写入当前内存（仅供 <see cref="PlanUtilizationFromInventory"/> 内部使用）。
    /// </summary>
    private InventoryUtilizationPlanResult SelectInventoryBoxesForUtilization(
        IEnumerable<BoxDimension> dimensions,
        double maxSelectableWeightKg)
    {
        if (!double.IsFinite(maxSelectableWeightKg) || maxSelectableWeightKg <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSelectableWeightKg), "最大选箱重量必须是有限正数。 ");

        var requestedDimensions = dimensions.ToArray();
        _logger.Log(
            PlannerLogLevel.Debug,
            "utilization.selection.begin",
            "开始执行库存选箱阶段。",
            detail: $"RequestedCount={requestedDimensions.Length}, Requested={DescribeDimensions(requestedDimensions)}, MaxSelectableWeightKg={maxSelectableWeightKg:R}");

        if (!TryBuildSucceededBaseline(out var succeededBaseline, out string baselineError))
        {
            _logger.Log(
                PlannerLogLevel.Warning,
                "utilization.selection.baseline_failed",
                "成功基线构建失败。",
                detail: baselineError);
            return new InventoryUtilizationPlanResult
            {
                PlanningResult = false,
                Message = baselineError,
                PlannedCounts = Array.Empty<BoxTypeCount>(),
                Layers = Array.Empty<LayerUtilizationSummary>(),
                Utilization = 0,
                Applied = false,
                SelectedBoxCount = 0,
            };
        }

        _logger.Log(
            PlannerLogLevel.Debug,
            "utilization.selection.baseline",
            "成功基线构建完成。",
            detail: $"SucceededCount={succeededBaseline.Boxes.Count}, BaselinePlacementCount={succeededBaseline.Placements.Count}, SucceededTypes={DescribeBoxTypeSummary(succeededBaseline.Boxes)}");

        var excludedNumbers = succeededBaseline.Boxes
            .Select(x => x.BoxNumber)
            .ToHashSet(StringComparer.Ordinal);
        var remainingDimensions = ReduceMinimumCountsBySucceeded(requestedDimensions, succeededBaseline.Boxes);
        _logger.Log(
            PlannerLogLevel.Debug,
            "utilization.selection.minimums_reduced",
            "已根据成功基线扣减候选尺寸最小数量。",
            detail: $"RemainingDimensions={DescribeDimensions(remainingDimensions)}");

        if (!TryBuildSucceededLayerFillPlan(succeededBaseline, out var fillPlan, out string fillPlanError))
        {
            _logger.Log(
                PlannerLogLevel.Warning,
                "utilization.selection.fill_plan_failed",
                "成功层补齐计划构建失败。",
                detail: fillPlanError);
            return new InventoryUtilizationPlanResult
            {
                PlanningResult = false,
                Message = fillPlanError,
                PlannedCounts = Array.Empty<BoxTypeCount>(),
                Layers = Array.Empty<LayerUtilizationSummary>(),
                Utilization = 0,
                Applied = false,
                SelectedBoxCount = 0,
            };
        }

        _logger.Log(
            PlannerLogLevel.Debug,
            "utilization.selection.fill_plan",
            "成功层补齐计划构建完成。",
            detail: fillPlan is null
                ? "FillPlan=None"
                : $"TypeId={fillPlan.TypeId}, NeededCount={fillPlan.NeededCount}, Dimension={DescribeDimension(fillPlan.Dimension)}");

        var inventory = ClassifyInventoryBoxTypes();
        var strategyExcludedNumbers = new HashSet<string>(excludedNumbers, StringComparer.Ordinal);
        var selectedBoxes = new List<Box>();

        if (fillPlan is not null)
        {
            // 成功层补齐后，不再把该箱型作为后续利用率候选尺寸。
            remainingDimensions = ExcludeCandidateDimension(remainingDimensions, fillPlan.Dimension);
            _logger.Log(
                PlannerLogLevel.Debug,
                "utilization.selection.fill_dimension_excluded",
                "已从后续候选尺寸剔除成功层补齐箱型。",
                detail: $"Excluded={DescribeDimension(fillPlan.Dimension)}, RemainingDimensions={DescribeDimensions(remainingDimensions)}");

            if (fillPlan.NeededCount > 0)
            {
                var sameTypeQueue = new Queue<BoxTypeAssignment>(inventory.Assignments
                    .Where(item => IsSelectableInventoryAssignment(item, maxSelectableWeightKg))
                    .Where(item => !strategyExcludedNumbers.Contains(item.BoxNumber))
                    .Where(item => string.Equals(item.TypeId, fillPlan.TypeId, StringComparison.Ordinal))
                    .OrderBy(item => item.BoxNumber, StringComparer.Ordinal));

                _logger.Log(
                    PlannerLogLevel.Debug,
                    "utilization.selection.fill_pool",
                    "成功层补齐候选池统计完成。",
                    detail: $"TypeId={fillPlan.TypeId}, NeededCount={fillPlan.NeededCount}, AvailableCount={sameTypeQueue.Count}");

                if (sameTypeQueue.Count < fillPlan.NeededCount)
                {
                    _logger.Log(
                        PlannerLogLevel.Warning,
                        "utilization.selection.fill_pool_insufficient",
                        "成功层补齐候选池不足。",
                        detail: $"TypeId={fillPlan.TypeId}, NeededCount={fillPlan.NeededCount}, AvailableCount={sameTypeQueue.Count}");
                    return new InventoryUtilizationPlanResult
                    {
                        PlanningResult = false,
                        Message = $"成功层补齐需要箱型 {fillPlan.TypeId} 至少 {fillPlan.NeededCount} 个，但库存可选仅 {sameTypeQueue.Count} 个。",
                        PlannedCounts = Array.Empty<BoxTypeCount>(),
                        Layers = Array.Empty<LayerUtilizationSummary>(),
                        Utilization = 0,
                        Applied = false,
                        SelectedBoxCount = 0,
                    };
                }

                var pickedNumbers = new List<string>(fillPlan.NeededCount);

                for (int i = 0; i < fillPlan.NeededCount; i++)
                {
                    var picked = sameTypeQueue.Dequeue();
                    pickedNumbers.Add(picked.BoxNumber);
                    selectedBoxes.Add(new Box
                    {
                        BoxNumber = picked.BoxNumber,
                        LengthMm = picked.DepthMm,
                        WidthMm = picked.LengthMm,
                        HeightMm = picked.HeightMm,
                        WeightKg = picked.WeightKg,
                        Status = BoxStatus.OnShelf,
                    });
                    strategyExcludedNumbers.Add(picked.BoxNumber);
                }

                _logger.Log(
                    PlannerLogLevel.Debug,
                    "utilization.selection.fill_picked",
                    "成功层补齐已选取真实箱号。",
                    detail: $"TypeId={fillPlan.TypeId}, PickedCount={pickedNumbers.Count}, PickedBoxes={string.Join(",", pickedNumbers.Take(12))}{(pickedNumbers.Count > 12 ? ",..." : string.Empty)}");
            }
        }

        // 成功基线已占用的堆垛高度（与基线分支 baseZ 口径一致），剩余高度才是策略可用的堆叠预算。
        double baselineHeightMm = CalculateSucceededBaselineHeight(succeededBaseline.Placements);
        double remainingStackHeightMm = Math.Max(0, StackMaxHeightMm - baselineHeightMm);
        _logger.Log(
            PlannerLogLevel.Debug,
            "utilization.selection.height_budget",
            "已扣除成功基线高度，计算策略可用堆垛高度。",
            detail: $"BaselineHeightMm={baselineHeightMm:R}, RemainingStackHeightMm={remainingStackHeightMm:R}");

        UtilizationPlanResult utilization;
        if (remainingDimensions.Count == 0)
        {
            utilization = new UtilizationPlanResult
            {
                PlannedCounts = Array.Empty<BoxTypeCount>(),
                Layers = Array.Empty<LayerUtilizationSummary>(),
                Utilization = 0,
                PlanningResult = true,
                Message = string.Empty,
            };
        }
        else
        {
            utilization = PlanBestUtilizationCore(
                remainingDimensions,
                maxSelectableWeightKg,
                strategyExcludedNumbers,
                remainingStackHeightMm);
        }

        if (!utilization.PlanningResult)
        {
            _logger.Log(
                PlannerLogLevel.Warning,
                "utilization.selection.strategy_failed",
                "候选尺寸策略规划失败。",
                detail: $"Message={utilization.Message}, PlannedCounts={DescribePlannedCounts(utilization.PlannedCounts)}, Utilization={utilization.Utilization:R}");
            return new InventoryUtilizationPlanResult
            {
                PlanningResult = false,
                Message = string.IsNullOrWhiteSpace(utilization.Message)
                    ? "无法满足箱型最小数量约束。"
                    : utilization.Message,
                PlannedCounts = utilization.PlannedCounts,
                Layers = utilization.Layers,
                Utilization = utilization.Utilization,
                Applied = false,
                SelectedBoxCount = 0,
            };
        }

        var typeByDimension = GetBoxTypeDefinitions()
            .GroupBy(
                item => ExactDimensionKey(item.RepLengthMm, item.RepDepthMm, item.RepHeightMm),
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.TypeId, StringComparer.Ordinal).First().TypeId,
                StringComparer.Ordinal);

        var poolByType = inventory.Assignments
            .Where(item => IsSelectableInventoryAssignment(item, maxSelectableWeightKg))
            .Where(item => !strategyExcludedNumbers.Contains(item.BoxNumber))
            .GroupBy(item => item.TypeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new Queue<BoxTypeAssignment>(
                    group.OrderBy(item => item.BoxNumber, StringComparer.Ordinal)),
                StringComparer.Ordinal);

        foreach (var planned in utilization.PlannedCounts.Where(item => item.Count > 0))
        {
            string key = ExactDimensionKey(planned.LengthMm, planned.WidthMm, planned.HeightMm);
            if (!typeByDimension.TryGetValue(key, out var typeId))
            {
                _logger.Log(
                    PlannerLogLevel.Warning,
                    "utilization.selection.dimension_unmapped",
                    "策略箱型无法映射到配置箱型。",
                    detail: $"Dimension={planned.LengthMm:0}x{planned.WidthMm:0}x{planned.HeightMm:0}, Count={planned.Count}");
                return new InventoryUtilizationPlanResult
                {
                    PlanningResult = false,
                    Message = $"策略箱型无法映射到 Core 配置：{planned.LengthMm:0}×{planned.WidthMm:0}×{planned.HeightMm:0}mm。",
                    PlannedCounts = utilization.PlannedCounts,
                    Layers = utilization.Layers,
                    Utilization = utilization.Utilization,
                    Applied = false,
                    SelectedBoxCount = 0,
                };
            }

            poolByType.TryGetValue(typeId, out var queue);
            int available = queue?.Count ?? 0;
            if (available < planned.Count)
            {
                _logger.Log(
                    PlannerLogLevel.Warning,
                    "utilization.selection.type_pool_insufficient",
                    "策略映射后的箱型库存池不足。",
                    detail: $"TypeId={typeId}, Needed={planned.Count}, Available={available}");
                return new InventoryUtilizationPlanResult
                {
                    PlanningResult = false,
                    Message = $"Core 库存中箱型 {typeId} 可用箱子不足：需要 {planned.Count}，可用 {available}。",
                    PlannedCounts = utilization.PlannedCounts,
                    Layers = utilization.Layers,
                    Utilization = utilization.Utilization,
                    Applied = false,
                    SelectedBoxCount = 0,
                };
            }

            for (int i = 0; i < planned.Count; i++)
            {
                var picked = queue!.Dequeue();
                // 维持既有协议：库存 depth -> 规划 length，库存 length -> 规划 width。
                selectedBoxes.Add(new Box
                {
                    BoxNumber = picked.BoxNumber,
                    LengthMm = picked.DepthMm,
                    WidthMm = picked.LengthMm,
                    HeightMm = picked.HeightMm,
                    WeightKg = picked.WeightKg,
                    Status = BoxStatus.OnShelf,
                });
            }
        }

        var snapshotBoxes = succeededBaseline.Boxes
            .Select(CloneBox)
            .Concat(selectedBoxes)
            .ToArray();
        var snapshotPlacements = succeededBaseline.Placements
            .Select(ClonePlacement)
            .ToArray();

        RestoreSnapshot(new PlannerSnapshot
        {
            Revision = 0,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Boxes = snapshotBoxes,
            Plan = new StackPlanResult
            {
                Placements = snapshotPlacements,
                PlanningResult = snapshotPlacements.Length == snapshotBoxes.Length,
            },
            ReusablePlacements = Array.Empty<ReusablePlacementSet>(),
        });

        string message = succeededBaseline.Boxes.Count == 0
            ? $"Core 已按最佳策略从库存选择 {selectedBoxes.Count} 个箱子。"
            : $"Core 已在 {succeededBaseline.Boxes.Count} 个成功堆垛基础上按最佳策略从库存选择 {selectedBoxes.Count} 个箱子。";

        _logger.Log(
            PlannerLogLevel.Debug,
            "utilization.selection.completed",
            "库存选箱阶段完成并已写入 Core 内存。",
            detail: $"SelectedBoxCount={selectedBoxes.Count}, SelectedTypes={DescribeBoxTypeSummary(selectedBoxes)}, PlannedCounts={DescribePlannedCounts(utilization.PlannedCounts)}, LayerCount={utilization.Layers.Count}, Utilization={utilization.Utilization:R}");

        return new InventoryUtilizationPlanResult
        {
            PlanningResult = true,
            Message = message,
            PlannedCounts = utilization.PlannedCounts,
            Layers = utilization.Layers,
            Utilization = utilization.Utilization,
            Applied = true,
            SelectedBoxCount = selectedBoxes.Count,
        };
    }

    /// <summary>
    /// 计算成功基线占用的堆垛高度（从托盘地面起算的最高点）。
    /// 与 <see cref="ToPlacedBox"/> 的 TopZ 口径一致：TopZ = PlaceFloorZMm - Zmm。
    /// </summary>
    private static double CalculateSucceededBaselineHeight(IReadOnlyList<BoxPlacement> placements)
        => placements.Count == 0
            ? 0
            : Math.Max(0, placements.Max(x => PlaceFloorZMm - x.Zmm));

    private bool TryBuildSucceededBaseline(
        out SucceededBaselineState baseline,
        out string message)
    {
        var boxes = new List<Box>();
        var placements = new List<BoxPlacement>();
        message = string.Empty;

        foreach (var box in _group.MutableBoxes
                     .Where(x => x.Status == BoxStatus.StackingSucceeded)
                     .OrderBy(x => x.Order)
                     .ThenBy(x => x.BoxNumber, StringComparer.Ordinal))
        {
            if (!_succeeded.TryGetValue(box.BoxNumber, out var fixedPlacement))
            {
                baseline = new SucceededBaselineState(Array.Empty<Box>(), Array.Empty<BoxPlacement>());
                message = $"箱子 {box.BoxNumber} 处于 StackingSucceeded 但缺少固定落点。";
                return false;
            }

            var placement = ClonePlacement(fixedPlacement.Placement);
            boxes.Add(CloneBox(box) with
            {
                Status = BoxStatus.StackingSucceeded,
                Order = placement.Order,
            });
            placements.Add(placement);
        }

        baseline = new SucceededBaselineState(
            boxes.ToArray(),
            placements
                .OrderBy(x => x.Order)
                .ThenBy(x => x.BoxNumber, StringComparer.Ordinal)
                .ToArray());
        return true;
    }

    private DirectStackResult? FindBestLayerStack(
        IReadOnlyList<DirectLayerResult> candidates,
        IReadOnlyList<BoxDimension> dimensions,
        IReadOnlyDictionary<string, int> caps,
        double maxStackHeightMm = StackMaxHeightMm)
    {
        var minimums = dimensions.Select(x => x.MinimumCount).ToArray();
        var remainingCaps = dimensions.Select(x => caps[DimensionKey(x)]).ToArray();
        var keys = dimensions.Select(DimensionKey).ToArray();
        var memo = new Dictionary<string, DirectStackResult?>();
        return SearchLayerStack(candidates, maxStackHeightMm, minimums, remainingCaps, keys, memo);
    }

    private DirectStackResult? SearchLayerStack(
        IReadOnlyList<DirectLayerResult> candidates,
        double remainingHeight,
        IReadOnlyList<int> remainingMinimums,
        IReadOnlyList<int> remainingCaps,
        IReadOnlyList<string> dimensionKeys,
        IDictionary<string, DirectStackResult?> memo)
    {
        string key = string.Join("|",
            Math.Round(remainingHeight, 3, MidpointRounding.AwayFromZero)
                .ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            string.Join(",", remainingMinimums),
            string.Join(",", remainingCaps));
        if (memo.TryGetValue(key, out var cached)) return cached;

        bool requirementsMet = remainingMinimums.All(x => x <= 0);
        DirectStackResult? best = requirementsMet
            ? new DirectStackResult(Array.Empty<DirectLayerResult>(), 0, 0, string.Empty)
            : null;
        foreach (var candidate in candidates
                     .Where(x => x.HeightMm <= remainingHeight + Epsilon)
                     .OrderByDescending(x => x.OccupiedArea / x.HeightMm)
                     .ThenBy(x => x.Signature, StringComparer.Ordinal))
        {
            var nextMinimums = remainingMinimums.ToArray();
            var nextCaps = remainingCaps.ToArray();
            bool withinCaps = true;
            foreach (var count in candidate.Placements
                         .GroupBy(x => DimensionKey(x.Dimension), StringComparer.Ordinal))
            {
                int index = Array.IndexOf(dimensionKeys.ToArray(), count.Key);
                if (index < 0) continue;
                int used = count.Count();
                // 库存上限：累计放置数量不能超过该箱型的剩余可选数量。
                if (used > nextCaps[index])
                {
                    withinCaps = false;
                    break;
                }
                nextCaps[index] -= used;
                nextMinimums[index] = Math.Max(0, nextMinimums[index] - used);
            }
            if (!withinCaps) continue;

            var tail = SearchLayerStack(
                candidates,
                remainingHeight - candidate.HeightMm,
                nextMinimums,
                nextCaps,
                dimensionKeys,
                memo);
            if (tail is null) continue;

            var plans = new[] { candidate }.Concat(tail.Layers).ToArray();
            var current = new DirectStackResult(
                plans,
                candidate.OccupiedArea + tail.TotalArea,
                candidate.Placements.Count + tail.TotalCount,
                string.Join("/", plans.Select(x => x.Signature)));
            if (best is null || IsBetterDirectStack(current, best)) best = current;
        }

        memo[key] = best;
        return best;
    }

    private static bool IsBetterDirectStack(DirectStackResult candidate, DirectStackResult current)
        => candidate.TotalArea > current.TotalArea + Epsilon
        || Math.Abs(candidate.TotalArea - current.TotalArea) <= Epsilon
        && (candidate.TotalCount > current.TotalCount
            || candidate.TotalCount == current.TotalCount
            && string.CompareOrdinal(candidate.Signature, current.Signature) < 0);

    private DirectLayerResult? FindBestDirectLayer(
        IReadOnlyList<BoxDimension> types,
        IReadOnlyDictionary<string, int> caps)
    {
        if (types.Count == 0) return null;
        var frontier = new[] { new DirectLayerState(Array.Empty<DirectLayerPlacement>(), 0) };
        DirectLayerState? best = null;
        for (int depth = 0; depth < 256 && frontier.Length > 0; depth++)
        {
            var next = new List<DirectLayerState>();
            foreach (var state in frontier)
            {
                // 已达到库存上限的箱型不再尝试放入本层。
                var open = types
                    .Where(type => state.Placements.Count(x => DimensionKey(x.Dimension) == DimensionKey(type))
                                   < caps[DimensionKey(type)])
                    .ToArray();
                foreach (var candidate in GenerateDirectCandidates(open, state.Placements))
                {
                    if (state.Placements.Any(x => DirectCollides(candidate, x))) continue;
                    next.Add(new DirectLayerState(
                        state.Placements.Concat(new[] { candidate }).ToArray(),
                        state.OccupiedArea + candidate.Width * candidate.Length));
                }
            }

            frontier = next
                .OrderBy(x => MissingDirectTypeCount(x, types))
                .ThenByDescending(x => x.OccupiedArea)
                .ThenByDescending(x => x.Placements.Count)
                .ThenBy(x => DirectFragmentation(x))
                .ThenBy(DirectSignature, StringComparer.Ordinal)
                .Take(BeamWidth)
                .ToArray();
            var roundBest = frontier
                .Where(x => MissingDirectTypeCount(x, types) == 0)
                .OrderByDescending(x => x.OccupiedArea)
                .ThenByDescending(x => x.Placements.Count)
                .ThenBy(x => DirectFragmentation(x))
                .ThenBy(DirectSignature, StringComparer.Ordinal)
                .FirstOrDefault();
            if (roundBest is not null && (best is null || IsBetterDirectState(roundBest, best)))
                best = roundBest;
        }

        if (best is null || best.Placements.Count == 0) return null;
        return new DirectLayerResult(types[0].HeightMm, best.Placements, best.OccupiedArea, DirectSignature(best));
    }

    private static int MissingDirectTypeCount(
        DirectLayerState state,
        IReadOnlyList<BoxDimension> types)
        => types.Count(type => !state.Placements.Any(x =>
            string.Equals(DimensionKey(x.Dimension), DimensionKey(type), StringComparison.Ordinal)));

    private IEnumerable<DirectLayerPlacement> GenerateDirectCandidates(
        IReadOnlyList<BoxDimension> types,
        IReadOnlyList<DirectLayerPlacement> placed)
    {
        foreach (var dimension in types)
        foreach (var orientation in dimension.LengthMm == dimension.WidthMm ? new[] { 0 } : new[] { 0, 90 })
        {
            double width = orientation == 0 ? dimension.WidthMm : dimension.LengthMm;
            double length = orientation == 0 ? dimension.LengthMm : dimension.WidthMm;
            var xs = new SortedSet<double> { _palletXMinMm + width / 2 };
            var ys = new SortedSet<double> { _palletYMinMm + length / 2 };
            foreach (var item in placed)
            {
                xs.Add(item.Left - StackBoxGapMm - width / 2);
                xs.Add(item.Right + StackBoxGapMm + width / 2);
                ys.Add(item.Bottom - StackBoxGapMm - length / 2);
                ys.Add(item.Top + StackBoxGapMm + length / 2);
            }

            foreach (var x in xs)
            foreach (var y in ys)
            {
                var candidate = new DirectLayerPlacement(dimension, x, y, width, length, orientation);
                if (candidate.Left < _palletXMinMm - Epsilon || candidate.Right > _palletXMaxMm + Epsilon
                    || candidate.Bottom < _palletYMinMm - Epsilon || candidate.Top > _palletYMaxMm + Epsilon)
                    continue;
                if (placed.Any(item => DirectCollides(candidate, item))) continue;
                yield return candidate;
            }
        }
    }

    private static bool DirectCollides(DirectLayerPlacement a, DirectLayerPlacement b)
        => a.Left - StackBoxGapMm / 2 < b.Right + StackBoxGapMm / 2 - Epsilon
        && a.Right + StackBoxGapMm / 2 > b.Left - StackBoxGapMm / 2 + Epsilon
        && a.Bottom - StackBoxGapMm / 2 < b.Top + StackBoxGapMm / 2 - Epsilon
        && a.Top + StackBoxGapMm / 2 > b.Bottom - StackBoxGapMm / 2 + Epsilon;

    private static bool IsBetterDirectState(DirectLayerState candidate, DirectLayerState current)
        => candidate.OccupiedArea > current.OccupiedArea + Epsilon
        || Math.Abs(candidate.OccupiedArea - current.OccupiedArea) <= Epsilon
        && (candidate.Placements.Count > current.Placements.Count
            || candidate.Placements.Count == current.Placements.Count
            && string.CompareOrdinal(DirectSignature(candidate), DirectSignature(current)) < 0);

    private static double DirectFragmentation(DirectLayerState state)
    {
        if (state.Placements.Count == 0) return 0;
        double left = state.Placements.Min(x => x.Left);
        double right = state.Placements.Max(x => x.Right);
        double bottom = state.Placements.Min(x => x.Bottom);
        double top = state.Placements.Max(x => x.Top);
        return Math.Max(0, (right - left) * (top - bottom) - state.OccupiedArea);
    }

    private static string DirectSignature(DirectLayerState state) => string.Join(";", state.Placements
        .OrderBy(x => DimensionKey(x.Dimension), StringComparer.Ordinal)
        .ThenBy(x => x.Xmm).ThenBy(x => x.Ymm).ThenBy(x => x.OrientationDeg)
        .Select(x => string.Join("|", DimensionKey(x.Dimension), x.Xmm.ToString("R"), x.Ymm.ToString("R"), x.OrientationDeg)));

    private static UtilizationPlanResult EmptyUtilizationPlan(
        string message,
        bool planningResult = false) => new()
    {
        PlannedCounts = Array.Empty<BoxTypeCount>(),
        Layers = Array.Empty<LayerUtilizationSummary>(),
        Utilization = 0,
        PlanningResult = planningResult,
        Message = message,
    };


    /// <summary>
    /// 使用当前真实箱子集合执行有限数量的二维利用率规划，并直接返回逐箱堆垛数据。
    /// 本方法复用 <see cref="PlanBestUtilization(IEnumerable{BoxDimension}, double)" /> 的
    /// 同高度分层、最多三种箱型组合和二维候选搜索思路，不会重新生成箱子数量或箱号。
    /// </summary>
    /// <remarks>
    /// 利用率规划面向“当前仍在货架上的箱子集合”。调用方应在规划前完成箱子的增删；
    /// 每个真实箱子最多使用一次，输出中的箱号和 Order 与当前集合保持对应。
    /// 二维排布阶段使用箱型类别代表尺寸，而不是单箱原始尺寸。
    /// </remarks>
    public StackPlanResult GenerateBestUtilizationPlan()
    {
        _logger.Log(
            PlannerLogLevel.Debug,
            "utilization.plan.begin",
            "开始执行真实箱子利用率排布。",
            detail: $"TotalBoxes={_group.MutableBoxes.Count}, OnShelf={_group.MutableBoxes.Count(x => x.Status == BoxStatus.OnShelf)}, Succeeded={_group.MutableBoxes.Count(x => x.Status == BoxStatus.StackingSucceeded)}, StatusSummary={DescribeStatusSummary(_group.MutableBoxes)}");

        ApplyPendingUtilizationOrders();
        AssignOrders(preserveExistingOnShelf: true);
        var definitionsByTypeId = GetBoxTypeDefinitions()
            .ToDictionary(item => item.TypeId, item => item, StringComparer.Ordinal);
        var utilizationCollisionBoxes = BuildCollisionBoxesByNumber(
            _group.MutableBoxes,
            definitionsByTypeId);
        var onShelfBoxes = _group.MutableBoxes
            .Where(x => x.Status == BoxStatus.OnShelf)
            .OrderBy(x => x.Order)
            .ThenBy(x => x.BoxNumber, StringComparer.Ordinal)
            .ToArray();

        if (_group.MutableBoxes.Any(x => x.Status is not (BoxStatus.OnShelf or BoxStatus.StackingSucceeded)))
        {
            var unsupported = _group.MutableBoxes
                .Where(x => x.Status is not (BoxStatus.OnShelf or BoxStatus.StackingSucceeded))
                .Select(x => $"{x.BoxNumber}:{x.Status}")
                .Take(12)
                .ToArray();
            _logger.Log(
                PlannerLogLevel.Warning,
                "utilization.plan.invalid_status",
                "存在不支持的箱子状态，利用率排布已终止。",
                detail: $"UnsupportedCount={unsupported.Length}, Unsupported={string.Join(",", unsupported)}");

            _lastPlan = new StackPlanResult
            {
                Placements = Array.Empty<BoxPlacement>(),
                PlanningResult = false,
            };
            return ClonePlan(_lastPlan);
        }

        var succeededPlacements = new Dictionary<string, BoxPlacement>(StringComparer.Ordinal);
        var occupied = new List<PlacedBox>();
        foreach (var succeededBox in _group.MutableBoxes
                     .Where(x => x.Status == BoxStatus.StackingSucceeded)
                     .OrderBy(x => x.Order)
                     .ThenBy(x => x.BoxNumber, StringComparer.Ordinal))
        {
            if (!_succeeded.TryGetValue(succeededBox.BoxNumber, out var fixedData))
            {
                _logger.Log(
                    PlannerLogLevel.Warning,
                    "utilization.plan.succeeded_missing_fixed",
                    "成功箱缺少固定落点，利用率排布已终止。",
                    detail: $"BoxNumber={succeededBox.BoxNumber}",
                    boxNumber: succeededBox.BoxNumber);
                _lastPlan = new StackPlanResult
                {
                    Placements = Array.Empty<BoxPlacement>(),
                    PlanningResult = false,
                };
                return ClonePlan(_lastPlan);
            }

            var placement = ClonePlacement(fixedData.Placement);
            var placed = ToPlacedBox(utilizationCollisionBoxes[succeededBox.BoxNumber], placement, real: true);
            if (!Fits(placed, occupied))
            {
                _logger.Log(
                    PlannerLogLevel.Warning,
                    "utilization.plan.succeeded_collision",
                    "成功箱固定落点与当前占位冲突，利用率排布已终止。",
                    detail: $"BoxNumber={succeededBox.BoxNumber}, Order={placement.Order}, Layer={placement.LayerIndex}, X={placement.Xmm:R}, Y={placement.Ymm:R}, Z={placement.Zmm:R}",
                    boxNumber: succeededBox.BoxNumber);
                _lastPlan = new StackPlanResult
                {
                    Placements = Array.Empty<BoxPlacement>(),
                    PlanningResult = false,
                };
                return ClonePlan(_lastPlan);
            }

            occupied.Add(placed);
            succeededPlacements[succeededBox.BoxNumber] = placement;
            succeededBox.Order = placement.Order;
        }

        if (succeededPlacements.Count > 0)
        {
            _logger.Log(
                PlannerLogLevel.Debug,
                "utilization.plan.with_succeeded_baseline",
                "检测到成功基线，进入基线约束排布分支。",
                detail: $"SucceededCount={succeededPlacements.Count}, OnShelfCount={onShelfBoxes.Length}");
            var onShelfCollisionBoxes = onShelfBoxes
                .Select(box => utilizationCollisionBoxes[box.BoxNumber])
                .OrderBy(box => box.Order)
                .ThenBy(box => box.BoxNumber, StringComparer.Ordinal)
                .ToArray();
            return GenerateBestUtilizationPlanWithSucceededBaseline(
                onShelfCollisionBoxes,
                occupied,
                succeededPlacements,
                utilizationCollisionBoxes);
        }

        if (onShelfBoxes.Length == 0)
        {
            _logger.Log(
                PlannerLogLevel.Info,
                "utilization.plan.no_onshelf",
                "没有 OnShelf 箱子，利用率排布直接成功。",
                detail: $"SucceededCount={succeededPlacements.Count}");
            _lastPlacements.Clear();
            _lastPlan = new StackPlanResult
            {
                Placements = Array.Empty<BoxPlacement>(),
                PlanningResult = true,
            };
            return ClonePlan(_lastPlan);
        }

        var planBoxes = BuildUtilizationPlanBoxes(onShelfBoxes);
        var remaining = planBoxes.ToList();
        var placements = new List<BoxPlacement>();
        double baseZ = 0;
        int layerIndex = 0;
        while (remaining.Count > 0)
        {
            var layerCandidates = new List<DirectBoxLayerResult>();
            foreach (var heightGroup in remaining.GroupBy(x => x.PlanHeightMm).OrderBy(x => x.Key))
            {
                if (baseZ + heightGroup.Key > StackMaxHeightMm + Epsilon)
                    continue;

                var typeKeys = heightGroup
                    .Select(x => x.TypeKey)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();
                foreach (var combination in TypeCombinations(typeKeys, 3))
                {
                    var candidate = FindBestFiniteDirectLayer(
                        remaining.Where(x => combination.Contains(x.TypeKey, StringComparer.Ordinal)).ToArray(),
                        heightGroup.Key,
                        combination.ToHashSet(StringComparer.Ordinal));
                    if (candidate is not null)
                        layerCandidates.Add(candidate);
                }
            }

            var selected = layerCandidates
                .OrderByDescending(x => x.OccupiedArea)
                .ThenByDescending(x => x.Placements.Count)
                .ThenBy(x => DirectBoxFragmentation(x.Placements))
                .ThenBy(x => x.HeightMm)
                .ThenBy(x => x.Signature, StringComparer.Ordinal)
                .FirstOrDefault();
            if (selected is null)
            {
                _logger.Log(
                    PlannerLogLevel.Warning,
                    "utilization.plan.layer_unplaceable",
                    "当前层未找到合法布局，终止后续层排布。",
                    detail: $"RemainingCount={remaining.Count}, BaseZ={baseZ:R}, LayerIndex={layerIndex}, RemainingTypes={DescribeUtilizationPlanBoxSummary(remaining)}");
                break;
            }

            foreach (var item in selected.Placements)
            {
                placements.Add(new BoxPlacement
                {
                    Order = item.Box.Source.Order,
                    BoxNumber = item.Box.Source.BoxNumber,
                    Xmm = Round(item.Xmm),
                    Ymm = Round(item.Ymm),
                    Zmm = Round(PlaceFloorZMm - baseZ - item.Box.PlanHeightMm),
                    OrientationDeg = item.OrientationDeg,
                    LayerIndex = layerIndex,
                });
            }

            var usedNumbers = selected.Placements
                .Select(x => x.Box.Source.BoxNumber)
                .ToHashSet(StringComparer.Ordinal);
            remaining.RemoveAll(x => usedNumbers.Contains(x.Source.BoxNumber));
            baseZ += selected.HeightMm;
            layerIndex++;
        }

        var orderedNew = placements
            .OrderBy(x => x.LayerIndex)
            .ThenByDescending(x => x.Xmm)
            .ThenByDescending(x => x.Ymm)
            .ThenBy(x => x.Order)
            .ThenBy(x => x.BoxNumber, StringComparer.Ordinal)
            .ToArray();
        orderedNew = NormalizePlacementOrders(orderedNew);

        foreach (var placement in orderedNew)
            FindBox(placement.BoxNumber)!.Order = placement.Order;

        var ordered = orderedNew
            .OrderBy(x => x.Order)
            .ThenBy(x => x.BoxNumber, StringComparer.Ordinal)
            .ToArray();

        _lastPlacements.Clear();
        foreach (var placement in ordered)
            _lastPlacements[placement.BoxNumber] = ClonePlacement(placement);
        _lastPlan = new StackPlanResult
        {
            Placements = ordered,
            PlanningResult = ordered.Length == onShelfBoxes.Length,
        };
        _logger.Log(
            _lastPlan.PlanningResult ? PlannerLogLevel.Info : PlannerLogLevel.Warning,
            "utilization.plan.completed",
            "真实箱子利用率排布完成。",
            detail: $"PlanningResult={_lastPlan.PlanningResult}, Placements={ordered.Length}, OnShelfCount={onShelfBoxes.Length}, Unplaced={Math.Max(0, onShelfBoxes.Length - ordered.Length)}");
        return ClonePlan(_lastPlan);
    }

    private StackPlanResult GenerateBestUtilizationPlanWithSucceededBaseline(
        IReadOnlyList<Box> onShelfBoxes,
        IReadOnlyList<PlacedBox> baselineOccupied,
        IReadOnlyDictionary<string, BoxPlacement> lockedPlacements,
        IReadOnlyDictionary<string, Box> collisionBoxesByNumber)
    {
        _logger.Log(
            PlannerLogLevel.Debug,
            "utilization.plan.baseline.begin",
            "开始执行成功基线约束下的利用率排布。",
            detail: $"OnShelfCount={onShelfBoxes.Count}, BaselineOccupied={baselineOccupied.Count}, LockedPlacementCount={lockedPlacements.Count}");

        if (onShelfBoxes.Count == 0)
        {
            var lockedOnly = lockedPlacements.Values
                .OrderBy(x => x.Order)
                .ThenBy(x => x.BoxNumber, StringComparer.Ordinal)
                .Select(ClonePlacement)
                .ToArray();

            _lastPlacements.Clear();
            foreach (var placement in lockedOnly)
                _lastPlacements[placement.BoxNumber] = ClonePlacement(placement);

            _lastPlan = new StackPlanResult
            {
                Placements = lockedOnly,
                PlanningResult = true,
            };
            _logger.Log(
                PlannerLogLevel.Info,
                "utilization.plan.baseline.completed",
                "成功基线约束排布完成（无 OnShelf 箱子）。",
                detail: $"PlanningResult=True, Placements={lockedOnly.Length}");
            return ClonePlan(_lastPlan);
        }

        int targetLayer = baselineOccupied.Max(x => x.Placement.LayerIndex);
        var targetLayerBoxes = baselineOccupied
            .Where(x => x.Placement.LayerIndex == targetLayer)
            .ToArray();
        var typedLayerBoxes = targetLayerBoxes
            .Select(placed =>
            {
                if (!collisionBoxesByNumber.TryGetValue(placed.Placement.BoxNumber, out var sourceBox))
                    return (Found: false, Placed: placed, Source: default(Box), TypeId: string.Empty);

                string typeId = BoxTypeClassifier.ClassifyPlanBoxTypeId(
                    sourceBox.LengthMm,
                    sourceBox.WidthMm,
                    sourceBox.HeightMm);
                return (Found: true, Placed: placed, Source: sourceBox, TypeId: typeId);
            })
            .ToArray();

        if (typedLayerBoxes.Any(x => !x.Found))
        {
            _logger.Log(
                PlannerLogLevel.Warning,
                "utilization.plan.baseline.type_resolve_failed",
                "成功层存在无法解析尺寸的箱子，排布失败。",
                detail: $"TargetLayer={targetLayer}, TargetLayerCount={targetLayerBoxes.Length}");
            _lastPlan = new StackPlanResult
            {
                Placements = Array.Empty<BoxPlacement>(),
                PlanningResult = false,
            };
            return ClonePlan(_lastPlan);
        }

        var succeededTypeSample = typedLayerBoxes
            .GroupBy(x => x.TypeId, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(x => x.Placed.Placement.Order)
                .ThenBy(x => x.Placed.Placement.BoxNumber, StringComparer.Ordinal)
                .First())
            .First();

        string succeededTypeId = succeededTypeSample.TypeId;
        double targetBaseZ = succeededTypeSample.Placed.BaseZ;
        var sameTypeTemplate = new Box
        {
            BoxNumber = "__succeeded_layer_probe__",
            LengthMm = succeededTypeSample.Source!.LengthMm,
            WidthMm = succeededTypeSample.Source.WidthMm,
            HeightMm = succeededTypeSample.Source.HeightMm,
            WeightKg = 0,
            Status = BoxStatus.OnShelf,
            Order = 0,
        };

        var planBoxes = BuildUtilizationPlanBoxes(onShelfBoxes);
        var occupied = baselineOccupied.ToList();
        var mergedPlacements = new Dictionary<string, BoxPlacement>(StringComparer.Ordinal);
        foreach (var locked in lockedPlacements.Values)
            mergedPlacements[locked.BoxNumber] = ClonePlacement(locked);

        var placedSucceededTypeNumbers = new HashSet<string>(StringComparer.Ordinal);
        int sameTypeCandidateCount = planBoxes.Count(x => string.Equals(x.TypeId, succeededTypeId, StringComparison.Ordinal));
        foreach (var planBox in planBoxes
                     .Where(x => string.Equals(x.TypeId, succeededTypeId, StringComparison.Ordinal))
                     .OrderBy(x => x.Source.Order)
                     .ThenBy(x => x.Source.BoxNumber, StringComparer.Ordinal))
        {
            var box = planBox.Source;
            var candidate = GenerateCandidates(box, occupied)
                .FirstOrDefault(x => x.Placement.LayerIndex == targetLayer
                    && Math.Abs(x.Candidate.BaseZ - targetBaseZ) <= Epsilon);
            if (candidate is null)
                continue;

            var placed = ToPlacedBox(box, candidate.Placement, false);
            if (!Fits(placed, occupied))
                continue;

            mergedPlacements[box.BoxNumber] = candidate.Placement;
            occupied.Add(placed);
            placedSucceededTypeNumbers.Add(box.BoxNumber);
        }

        _logger.Log(
            PlannerLogLevel.Debug,
            "utilization.plan.baseline.fill_attempt",
            "已尝试补齐成功层同型箱。",
            detail: $"SucceededTypeId={succeededTypeId}, CandidateCount={sameTypeCandidateCount}, PlacedCount={placedSucceededTypeNumbers.Count}, TargetLayer={targetLayer}, TargetBaseZ={targetBaseZ:R}");

        int openSlotCount = GenerateCandidates(sameTypeTemplate, occupied)
            .Count(x => x.Placement.LayerIndex == targetLayer
                && Math.Abs(x.Candidate.BaseZ - targetBaseZ) <= Epsilon);
        bool layerIsFull = openSlotCount == 0;
        if (!layerIsFull)
        {
            _logger.Log(
                PlannerLogLevel.Warning,
                "utilization.plan.baseline.layer_not_full",
                "成功层同型补齐后仍存在可放空位，排布失败。",
                detail: $"SucceededTypeId={succeededTypeId}, OpenSlotCount={openSlotCount}, PlacedCount={placedSucceededTypeNumbers.Count}, CandidateCount={sameTypeCandidateCount}, TargetLayer={targetLayer}, TargetBaseZ={targetBaseZ:R}");
            _lastPlan = new StackPlanResult
            {
                Placements = Array.Empty<BoxPlacement>(),
                PlanningResult = false,
            };
            return ClonePlan(_lastPlan);
        }

        var remaining = planBoxes
            .Where(x => !placedSucceededTypeNumbers.Contains(x.Source.BoxNumber)
                && !string.Equals(x.TypeId, succeededTypeId, StringComparison.Ordinal))
            .ToList();
        int remainingBoxCount = remaining.Count;
        var additionalPlacements = new List<BoxPlacement>();
        double baseZ = occupied.Max(x => x.TopZ);
        int layerIndex = occupied.Max(x => x.Placement.LayerIndex) + 1;

        while (remaining.Count > 0)
        {
            var layerCandidates = new List<DirectBoxLayerResult>();
            foreach (var heightGroup in remaining.GroupBy(x => x.PlanHeightMm).OrderBy(x => x.Key))
            {
                if (baseZ + heightGroup.Key > StackMaxHeightMm + Epsilon)
                    continue;

                var typeKeys = heightGroup
                    .Select(x => x.TypeKey)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();
                foreach (var combination in TypeCombinations(typeKeys, 3))
                {
                    var candidate = FindBestFiniteDirectLayer(
                        remaining.Where(x => combination.Contains(x.TypeKey, StringComparer.Ordinal)).ToArray(),
                        heightGroup.Key,
                        combination.ToHashSet(StringComparer.Ordinal));
                    if (candidate is not null)
                        layerCandidates.Add(candidate);
                }
            }

            var selected = layerCandidates
                .OrderByDescending(x => x.OccupiedArea)
                .ThenByDescending(x => x.Placements.Count)
                .ThenBy(x => DirectBoxFragmentation(x.Placements))
                .ThenBy(x => x.HeightMm)
                .ThenBy(x => x.Signature, StringComparer.Ordinal)
                .FirstOrDefault();
            if (selected is null)
            {
                _logger.Log(
                    PlannerLogLevel.Warning,
                    "utilization.plan.baseline.layer_unplaceable",
                    "成功基线约束下，后续层未找到合法布局。",
                    detail: $"RemainingCount={remaining.Count}, BaseZ={baseZ:R}, LayerIndex={layerIndex}, RemainingTypes={DescribeUtilizationPlanBoxSummary(remaining)}");
                break;
            }

            foreach (var item in selected.Placements)
            {
                var placement = new BoxPlacement
                {
                    Order = item.Box.Source.Order,
                    BoxNumber = item.Box.Source.BoxNumber,
                    Xmm = Round(item.Xmm),
                    Ymm = Round(item.Ymm),
                    Zmm = Round(PlaceFloorZMm - baseZ - item.Box.PlanHeightMm),
                    OrientationDeg = item.OrientationDeg,
                    LayerIndex = layerIndex,
                };
                additionalPlacements.Add(placement);
                occupied.Add(ToPlacedBox(item.Box.Source, placement, false));
            }

            var usedNumbers = selected.Placements
                .Select(x => x.Box.Source.BoxNumber)
                .ToHashSet(StringComparer.Ordinal);
            remaining.RemoveAll(x => usedNumbers.Contains(x.Source.BoxNumber));
            baseZ += selected.HeightMm;
            layerIndex++;
        }

        foreach (var placement in additionalPlacements)
            mergedPlacements[placement.BoxNumber] = placement;

        var normalized = AppendAfterSucceededOrders(
            new SearchState(
                Array.Empty<Box>(),
                occupied.ToArray(),
                mergedPlacements),
            _group.MutableBoxes);

        var ordered = normalized.Placements.Values
            .OrderBy(x => x.Order)
            .ThenBy(x => x.BoxNumber, StringComparer.Ordinal)
            .ToArray();
        foreach (var placement in ordered)
            FindBox(placement.BoxNumber)!.Order = placement.Order;

        _lastPlacements.Clear();
        foreach (var placement in ordered)
            _lastPlacements[placement.BoxNumber] = ClonePlacement(placement);

        int expected = lockedPlacements.Count
            + placedSucceededTypeNumbers.Count
            + remainingBoxCount;
        _lastPlan = new StackPlanResult
        {
            Placements = ordered,
            PlanningResult = ordered.Length == expected,
        };
        _logger.Log(
            _lastPlan.PlanningResult ? PlannerLogLevel.Info : PlannerLogLevel.Warning,
            "utilization.plan.baseline.completed",
            "成功基线约束排布完成。",
            detail: $"PlanningResult={_lastPlan.PlanningResult}, Placements={ordered.Length}, Expected={expected}, LockedCount={lockedPlacements.Count}, FilledSucceededType={placedSucceededTypeNumbers.Count}, RemainingInitial={remainingBoxCount}");
        return ClonePlan(_lastPlan);
    }

    private static SearchState AppendAfterSucceededOrders(SearchState state, IReadOnlyList<Box> all)
    {
        var boxesByNumber = all.ToDictionary(x => x.BoxNumber, StringComparer.Ordinal);
        var succeededNumbers = all
            .Where(x => x.Status == BoxStatus.StackingSucceeded)
            .Select(x => x.BoxNumber)
            .ToHashSet(StringComparer.Ordinal);

        var replacements = new Dictionary<string, BoxPlacement>(StringComparer.Ordinal);
        foreach (var placement in state.Placements.Values.Where(x => succeededNumbers.Contains(x.BoxNumber)))
            replacements[placement.BoxNumber] = placement;

        int nextOrder = replacements.Values.Select(x => x.Order).DefaultIfEmpty(-1).Max() + 1;
        foreach (var placement in state.Placements.Values
                     .Where(x => !succeededNumbers.Contains(x.BoxNumber))
                     .OrderBy(x => x.LayerIndex)
                     .ThenByDescending(x => x.Xmm)
                     .ThenByDescending(x => x.Ymm)
                     .ThenByDescending(x => BoxBaseArea(boxesByNumber[x.BoxNumber]))
                     .ThenBy(x => BoxTypeKey(boxesByNumber[x.BoxNumber]), StringComparer.Ordinal)
                     .ThenBy(x => boxesByNumber[x.BoxNumber].Order)
                     .ThenBy(x => x.BoxNumber, StringComparer.Ordinal))
        {
            replacements[placement.BoxNumber] = placement with { Order = nextOrder++ };
        }

        return state with { Placements = replacements };
    }

    private static IReadOnlyList<UtilizationPlanBox> BuildUtilizationPlanBoxes(
        IReadOnlyList<Box> boxes)
    {
        var definitionsByTypeId = BoxTypeClassifier.GetDefinitions()
            .ToDictionary(item => item.TypeId, item => item, StringComparer.Ordinal);

        var result = new List<UtilizationPlanBox>(boxes.Count);
        foreach (var box in boxes)
        {
            string typeId = BoxTypeClassifier.ClassifyPlanBoxTypeId(
                box.LengthMm,
                box.WidthMm,
                box.HeightMm);
            double lengthMm = box.LengthMm;
            double widthMm = box.WidthMm;
            double heightMm = box.HeightMm;

            if (definitionsByTypeId.TryGetValue(typeId, out var definition))
            {
                // 配置语义：Length=RepLength，Width=RepDepth，Height=RepHeight。
                lengthMm = definition.RepLengthMm;
                widthMm = definition.RepDepthMm;
                heightMm = definition.RepHeightMm;
            }

            result.Add(new UtilizationPlanBox(
                Source: box,
                TypeId: typeId,
                PlanLengthMm: lengthMm,
                PlanWidthMm: widthMm,
                PlanHeightMm: heightMm,
                TypeKey: DimensionKey(new BoxDimension
                {
                    LengthMm = lengthMm,
                    WidthMm = widthMm,
                    HeightMm = heightMm,
                })));
        }

        return result;
    }

    private static Dictionary<string, Box> BuildCollisionBoxesByNumber(
        IReadOnlyList<Box> boxes,
        IReadOnlyDictionary<string, BoxTypeDefinition> definitionsByTypeId)
    {
        var mapped = new Dictionary<string, Box>(StringComparer.Ordinal);
        foreach (var box in boxes)
        {
            var collisionBox = ToCollisionSizedBox(box, definitionsByTypeId);
            mapped[box.BoxNumber] = collisionBox;
        }

        return mapped;
    }

    private static Box ToCollisionSizedBox(
        Box box,
        IReadOnlyDictionary<string, BoxTypeDefinition> definitionsByTypeId)
    {
        var collisionBox = CloneBox(box);
        var definition = FindBestCollisionDefinition(box, definitionsByTypeId);
        if (definition is null)
            return collisionBox;

        // 碰撞体统一按箱型标准尺寸，避免真实尺寸与规划尺寸口径切换。
        return collisionBox with
        {
            LengthMm = definition.RepLengthMm,
            WidthMm = definition.RepDepthMm,
            HeightMm = definition.RepHeightMm,
        };
    }

    private static BoxTypeDefinition? FindBestCollisionDefinition(
        Box box,
        IReadOnlyDictionary<string, BoxTypeDefinition> definitionsByTypeId)
    {
        const double toleranceMm = 30;

        if (!double.IsFinite(box.LengthMm) || !double.IsFinite(box.WidthMm) || !double.IsFinite(box.HeightMm)
            || box.LengthMm <= 0 || box.WidthMm <= 0 || box.HeightMm <= 0)
        {
            return null;
        }

        BoxTypeDefinition? best = null;
        string? bestTypeId = null;
        double bestDistance = double.MaxValue;

        foreach (var entry in definitionsByTypeId)
        {
            var definition = entry.Value;
            if (Math.Abs(box.LengthMm - definition.RepLengthMm) > toleranceMm
                || Math.Abs(box.HeightMm - definition.RepHeightMm) > toleranceMm
                || Math.Abs(box.WidthMm - definition.RepDepthMm) > toleranceMm)
            {
                continue;
            }

            double distance = Math.Abs(box.LengthMm - definition.RepLengthMm)
                + Math.Abs(box.HeightMm - definition.RepHeightMm)
                + Math.Abs(box.WidthMm - definition.RepDepthMm);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = definition;
                bestTypeId = entry.Key;
                continue;
            }

            if (Math.Abs(distance - bestDistance) <= 0.0001
                && bestTypeId is not null
                && string.CompareOrdinal(entry.Key, bestTypeId) < 0)
            {
                best = definition;
                bestTypeId = entry.Key;
            }
        }

        return best;
    }

    private DirectBoxLayerResult? FindBestFiniteDirectLayer(
        IReadOnlyList<UtilizationPlanBox> boxes,
        double heightMm,
        IReadOnlySet<string> allowedTypeKeys)
    {
        if (boxes.Count == 0)
            return null;

        var frontier = new[] { new DirectBoxLayerState(Array.Empty<DirectBoxLayerPlacement>(), 0, boxes) };
        DirectBoxLayerState? best = null;
        for (int depth = 0; depth < boxes.Count && frontier.Length > 0; depth++)
        {
            var next = new List<DirectBoxLayerState>();
            foreach (var state in frontier)
            {
                var representatives = state.Remaining
                    .Where(x => allowedTypeKeys.Contains(x.TypeKey))
                    .GroupBy(x => x.TypeKey, StringComparer.Ordinal)
                    .Select(group => group.OrderBy(x => x.Source.Order).ThenBy(x => x.Source.BoxNumber, StringComparer.Ordinal).First())
                    .OrderBy(x => x.TypeKey, StringComparer.Ordinal)
                    .ToArray();
                foreach (var candidate in GenerateFiniteDirectCandidates(representatives, state.Placements))
                {
                    if (state.Placements.Any(x => DirectBoxCollides(candidate, x)))
                        continue;
                    var remaining = state.Remaining
                        .Where(x => !string.Equals(x.Source.BoxNumber, candidate.Box.Source.BoxNumber, StringComparison.Ordinal))
                        .ToArray();
                    next.Add(new DirectBoxLayerState(
                        state.Placements.Concat(new[] { candidate }).ToArray(),
                        state.OccupiedArea + candidate.Width * candidate.Length,
                        remaining));
                }
            }

            frontier = next
                .OrderBy(x => MissingDirectBoxTypeCount(x, allowedTypeKeys))
                .ThenByDescending(x => x.OccupiedArea)
                .ThenByDescending(x => x.Placements.Count)
                .ThenBy(x => DirectBoxFragmentation(x.Placements))
                .ThenBy(DirectBoxSignature, StringComparer.Ordinal)
                .Take(BeamWidth)
                .ToArray();
            var roundBest = frontier
                .Where(x => MissingDirectBoxTypeCount(x, allowedTypeKeys) == 0)
                .OrderByDescending(x => x.OccupiedArea)
                .ThenByDescending(x => x.Placements.Count)
                .ThenBy(x => DirectBoxFragmentation(x.Placements))
                .ThenBy(DirectBoxSignature, StringComparer.Ordinal)
                .FirstOrDefault();
            if (roundBest is not null && (best is null || IsBetterFiniteDirectState(roundBest, best)))
                best = roundBest;
        }

        return best is null || best.Placements.Count == 0
            ? null
            : new DirectBoxLayerResult(heightMm, best.Placements, best.OccupiedArea, DirectBoxSignature(best));
    }

    private IEnumerable<DirectBoxLayerPlacement> GenerateFiniteDirectCandidates(
        IReadOnlyList<UtilizationPlanBox> boxes,
        IReadOnlyList<DirectBoxLayerPlacement> placed)
    {
        foreach (var box in boxes)
        foreach (var orientation in box.PlanLengthMm == box.PlanWidthMm ? new[] { 0 } : new[] { 0, 90 })
        {
            double width = orientation == 0 ? box.PlanWidthMm : box.PlanLengthMm;
            double length = orientation == 0 ? box.PlanLengthMm : box.PlanWidthMm;
            var xs = new SortedSet<double> { _palletXMinMm + width / 2 };
            var ys = new SortedSet<double> { _palletYMinMm + length / 2 };
            foreach (var item in placed)
            {
                xs.Add(item.Left - StackBoxGapMm - width / 2);
                xs.Add(item.Right + StackBoxGapMm + width / 2);
                ys.Add(item.Bottom - StackBoxGapMm - length / 2);
                ys.Add(item.Top + StackBoxGapMm + length / 2);
            }

            foreach (var x in xs)
            foreach (var y in ys)
            {
                var candidate = new DirectBoxLayerPlacement(box, x, y, width, length, orientation);
                if (candidate.Left < _palletXMinMm - Epsilon || candidate.Right > _palletXMaxMm + Epsilon
                    || candidate.Bottom < _palletYMinMm - Epsilon || candidate.Top > _palletYMaxMm + Epsilon)
                    continue;
                if (placed.Any(item => DirectBoxCollides(candidate, item)))
                    continue;
                yield return candidate;
            }
        }
    }

    private static int MissingDirectBoxTypeCount(
        DirectBoxLayerState state,
        IReadOnlySet<string> allowedTypeKeys)
        => allowedTypeKeys.Count(type => !state.Placements.Any(x => x.Box.TypeKey == type));

    private static bool IsBetterFiniteDirectState(DirectBoxLayerState candidate, DirectBoxLayerState current)
        => candidate.OccupiedArea > current.OccupiedArea + Epsilon
        || Math.Abs(candidate.OccupiedArea - current.OccupiedArea) <= Epsilon
        && (candidate.Placements.Count > current.Placements.Count
            || candidate.Placements.Count == current.Placements.Count
            && string.CompareOrdinal(DirectBoxSignature(candidate), DirectBoxSignature(current)) < 0);

    private static double DirectBoxFragmentation(IReadOnlyList<DirectBoxLayerPlacement> placements)
    {
        if (placements.Count == 0) return 0;
        double left = placements.Min(x => x.Left);
        double right = placements.Max(x => x.Right);
        double bottom = placements.Min(x => x.Bottom);
        double top = placements.Max(x => x.Top);
        double area = placements.Sum(x => x.Width * x.Length);
        return Math.Max(0, (right - left) * (top - bottom) - area);
    }

    private static string DirectBoxSignature(DirectBoxLayerState state)
        => string.Join(";", state.Placements
            .OrderBy(x => x.Box.TypeKey, StringComparer.Ordinal)
            .ThenBy(x => x.Xmm)
            .ThenBy(x => x.Ymm)
            .ThenBy(x => x.OrientationDeg)
            .ThenBy(x => x.Box.Source.BoxNumber, StringComparer.Ordinal)
            .Select(x => string.Join("|", x.Box.TypeKey,
                x.Xmm.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                x.Ymm.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                x.OrientationDeg, x.Box.Source.BoxNumber)));

    private static bool DirectBoxCollides(DirectBoxLayerPlacement a, DirectBoxLayerPlacement b)
        => a.Left - StackBoxGapMm / 2 < b.Right + StackBoxGapMm / 2 - Epsilon
        && a.Right + StackBoxGapMm / 2 > b.Left - StackBoxGapMm / 2 + Epsilon
        && a.Bottom - StackBoxGapMm / 2 < b.Top + StackBoxGapMm / 2 - Epsilon
        && a.Top + StackBoxGapMm / 2 > b.Bottom - StackBoxGapMm / 2 + Epsilon;
    private static BoxDimension ToDimension(Box box) => new()
    {
        LengthMm = box.LengthMm,
        WidthMm = box.WidthMm,
        HeightMm = box.HeightMm,
    };

    private static string DimensionKey(BoxDimension dimension)
        => string.Join("x", Math.Min(dimension.LengthMm, dimension.WidthMm).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            Math.Max(dimension.LengthMm, dimension.WidthMm).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            dimension.HeightMm.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    private static string ExactDimensionKey(double lengthMm, double widthMm, double heightMm)
        => string.Join("|",
            lengthMm.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            widthMm.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            heightMm.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    private static string DescribeDimension(BoxDimension dimension)
        => $"{dimension.LengthMm:0.###}x{dimension.WidthMm:0.###}x{dimension.HeightMm:0.###}(min={dimension.MinimumCount})";

    private static string DescribeDimensions(IEnumerable<BoxDimension> dimensions, int maxItems = 10)
    {
        var items = dimensions
            .Select(DescribeDimension)
            .ToArray();
        if (items.Length == 0)
            return "none";
        if (items.Length <= maxItems)
            return string.Join(";", items);
        return string.Join(";", items.Take(maxItems)) + ";...";
    }

    private static string DescribePlannedCounts(IEnumerable<BoxTypeCount> plannedCounts, int maxItems = 10)
    {
        var items = plannedCounts
            .Where(x => x.Count > 0)
            .Select(x => $"{x.LengthMm:0.###}x{x.WidthMm:0.###}x{x.HeightMm:0.###}={x.Count}")
            .ToArray();
        if (items.Length == 0)
            return "none";
        if (items.Length <= maxItems)
            return string.Join(";", items);
        return string.Join(";", items.Take(maxItems)) + ";...";
    }

    private static string DescribeStatusSummary(IEnumerable<Box> boxes)
    {
        var items = boxes
            .GroupBy(x => x.Status)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}:{group.Count()}")
            .ToArray();
        return items.Length == 0 ? "none" : string.Join(",", items);
    }

    private static string DescribeBoxTypeSummary(IEnumerable<Box> boxes, int maxItems = 10)
    {
        var items = boxes
            .GroupBy(SameSpecTypeId, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}:{group.Count()}")
            .ToArray();
        if (items.Length == 0)
            return "none";
        if (items.Length <= maxItems)
            return string.Join(",", items);
        return string.Join(",", items.Take(maxItems)) + ",...";
    }

    private static string DescribeUtilizationPlanBoxSummary(IEnumerable<UtilizationPlanBox> boxes, int maxItems = 10)
    {
        var items = boxes
            .GroupBy(x => x.TypeId, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}:{group.Count()}")
            .ToArray();
        if (items.Length == 0)
            return "none";
        if (items.Length <= maxItems)
            return string.Join(",", items);
        return string.Join(",", items.Take(maxItems)) + ",...";
    }

    /// <summary>
    /// 仅为利用率规划应用同 TypeId 替换箱的末尾序号。
    /// </summary>
    private void ApplyPendingUtilizationOrders()
    {
        foreach (var entry in _pendingReplacementBoxes.ToArray())
        {
            string typeKey = entry.Key;
            var replacementBoxes = _group.MutableBoxes
                .Where(x => entry.Value.Contains(x.BoxNumber)
                    && x.Status is not (BoxStatus.Stacking or BoxStatus.StackingSucceeded))
                .ToArray();
            if (replacementBoxes.Length == 0)
                continue;

            var eligible = _group.MutableBoxes
                .Where(x => x.Status is not (BoxStatus.Stacking or BoxStatus.StackingSucceeded)
                    && string.Equals(SameSpecTypeId(x), typeKey, StringComparison.Ordinal)
                    && !entry.Value.Contains(x.BoxNumber)
                    && x.Order >= 0)
                .ToArray();
            int nextOrder = eligible.Select(x => x.Order).DefaultIfEmpty(-1).Max() + 1;
            var usedOrders = _group.MutableBoxes.Where(x => x.Order >= 0).Select(x => x.Order).ToHashSet();
            foreach (var box in replacementBoxes.OrderBy(x => x.BoxNumber, StringComparer.Ordinal))
            {
                while (usedOrders.Contains(nextOrder)) nextOrder++;
                box.Order = nextOrder++;
                usedOrders.Add(box.Order);
            }

            _pendingReplacementBoxes.Remove(typeKey);
        }
    }

    private void RemoveUtilizationBox(string boxNumber)
    {
        var removed = FindBox(boxNumber)!;
        string removedKey = SameSpecTypeId(removed);
        var oldPlacements = _lastPlan.Placements.ToArray();
        var groups = oldPlacements
            .Where(point => FindBox(point.BoxNumber) is not null)
            .GroupBy(point => SameSpecTypeId(FindBox(point.BoxNumber)!), StringComparer.Ordinal)
            .ToArray();
        var replacements = new Dictionary<string, BoxPlacement>(StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var points = group.ToArray();
            var survivors = points
                .Where(point => !string.Equals(point.BoxNumber, boxNumber, StringComparison.Ordinal))
                .ToArray();
            var sourcePoints = points
                .Where(point => !string.Equals(point.BoxNumber, boxNumber, StringComparison.Ordinal))
                .ToArray();
            if (string.Equals(group.Key, removedKey, StringComparison.Ordinal))
            {
                sourcePoints = points;
                for (int i = 0; i < survivors.Length; i++)
                    replacements[survivors[i].BoxNumber] = sourcePoints[i] with
                    {
                        BoxNumber = survivors[i].BoxNumber,
                    };
                if (points.Length > survivors.Length)
                {
                    var tail = sourcePoints[^1];
                    _reusableUtilizationPlacements
                        .GetValueOrDefault(removedKey, new List<BoxPlacement>())
                        .Add(tail);
                    if (!_reusableUtilizationPlacements.ContainsKey(removedKey))
                        _reusableUtilizationPlacements[removedKey] = new List<BoxPlacement> { tail };
                    _reusablePlacementTypeDimensions[removedKey] = new BoxDimension
                    {
                        LengthMm = removed.LengthMm,
                        WidthMm = removed.WidthMm,
                        HeightMm = removed.HeightMm,
                    };
                }
            }
        }

        _group.MutableBoxes.Remove(removed);
        _lastPlacements.Clear();
        foreach (var point in oldPlacements)
        {
            if (string.Equals(point.BoxNumber, boxNumber, StringComparison.Ordinal))
                continue;
            var replacement = replacements.GetValueOrDefault(point.BoxNumber, point);
            _lastPlacements[replacement.BoxNumber] = replacement;
        }
        _lastPlan = new StackPlanResult
        {
            Placements = oldPlacements
                .Where(point => !string.Equals(point.BoxNumber, boxNumber, StringComparison.Ordinal))
                .Select(point => replacements.GetValueOrDefault(point.BoxNumber, point))
                .ToArray(),
            PlanningResult = true,
        };
    }

    private static BoxPlacement[] NormalizePlacementOrders(IReadOnlyList<BoxPlacement> placements)
        => placements
            .Select((placement, index) => placement with { Order = index })
            .ToArray();

    private UtilizationMutationResult MutationSuccess(string message)
        => new()
        {
            Succeeded = true,
            Message = message,
            Boxes = GetBoxes(),
            Plan = CurrentPlan,
            ReusablePlacements = ExportReusablePlacements(),
        };

    private UtilizationMutationResult MutationFailure(string message)
        => new()
        {
            Succeeded = false,
            Message = message,
            Boxes = GetBoxes(),
            Plan = CurrentPlan,
            ReusablePlacements = ExportReusablePlacements(),
        };

    private IReadOnlyList<ReusablePlacementSet> ExportReusablePlacements()
        => _reusableUtilizationPlacements
            .Where(entry => entry.Value.Count > 0)
            .Select(entry => new ReusablePlacementSet
            {
                Dimension = ResolveReusableDimensionByTypeId(entry.Key),
                Placements = entry.Value.Select(ClonePlacement).ToArray(),
            })
            .ToArray();

    private BoxDimension ResolveReusableDimensionByTypeId(string typeId)
    {
        var definition = BoxTypeClassifier.GetDefinitions()
            .FirstOrDefault(item => string.Equals(item.TypeId, typeId, StringComparison.Ordinal));
        if (definition is not null)
        {
            return new BoxDimension
            {
                LengthMm = definition.RepLengthMm,
                WidthMm = definition.RepDepthMm,
                HeightMm = definition.RepHeightMm,
            };
        }

        if (_reusablePlacementTypeDimensions.TryGetValue(typeId, out var dimension))
        {
            return new BoxDimension
            {
                LengthMm = dimension.LengthMm,
                WidthMm = dimension.WidthMm,
                HeightMm = dimension.HeightMm,
            };
        }

        if (TryParseDimensionKey(typeId, out var parsed))
            return parsed;

        return new BoxDimension
        {
            LengthMm = 0,
            WidthMm = 0,
            HeightMm = 0,
        };
    }

    private static bool TryParseDimensionKey(string key, out BoxDimension dimension)
    {
        dimension = default!;
        var values = key.Split('x');
        if (values.Length != 3)
            return false;

        if (!double.TryParse(values[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var length)
            || !double.TryParse(values[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var width)
            || !double.TryParse(values[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var height))
        {
            return false;
        }

        dimension = new BoxDimension
        {
            LengthMm = length,
            WidthMm = width,
            HeightMm = height,
        };
        return true;
    }

    private void AssignOrders(bool preserveExistingOnShelf = false)
    {
        // 非 OnShelf 箱子的 Order 是业务流程已经锁定的顺序，不能重新编号。
        // OnShelf 箱子严格按照加入集合的顺序分配顺序号，不再按箱型或箱号排序。
        var boxes = _group.MutableBoxes;
        if (boxes.Count > 0 && boxes.All(x => x.Status == BoxStatus.OnShelf))
        {
            if (!preserveExistingOnShelf)
            {
                int order = 0;
                foreach (var box in boxes)
                    box.Order = order++;
                return;
            }

            int nextOrder = boxes
                .Where(x => x.Order >= 0)
                .Select(x => x.Order)
                .DefaultIfEmpty(-1)
                .Max() + 1;
            foreach (var box in boxes.Where(x => x.Order < 0))
                box.Order = nextOrder++;
            return;
        }
        var used = boxes.Where(x => x.Status != BoxStatus.OnShelf && x.Order >= 0).Select(x => x.Order).ToHashSet();
        int next = used.Count == 0 ? 0 : used.Max() + 1;
        foreach (var box in boxes.Where(x => x.Status == BoxStatus.OnShelf))
        {
            if (box.Order >= 0)
            {
                used.Add(box.Order);
                continue;
            }
            while (used.Contains(next)) next++;
            box.Order = next++;
            used.Add(box.Order);
        }
        if (boxes.Any(x => x.Status != BoxStatus.OnShelf && x.Order < 0))
            throw new InvalidOperationException("非 OnShelf 箱子必须已有固定 Order。");
    }

    /// <summary>
    /// 生成一个箱子的全部合法候选位置，并按固定规则排序。
    /// </summary>
    private IReadOnlyList<CandidatePlacement> GenerateCandidates(Box box, IReadOnlyList<PlacedBox> occupied)
    {
        // 候选点由托盘边界和已占用箱体的边界共同产生，避免连续浮点网格搜索。
        var candidates = new List<CandidatePlacement>();
        foreach (var orientation in new[] { 0, 90 })
        {
            // 0° 使用箱子的原始长宽，90° 交换 X/Y 方向尺寸。
            double width = orientation == 0 ? box.WidthMm : box.LengthMm;
            double length = orientation == 0 ? box.LengthMm : box.WidthMm;
            var xValues = new SortedSet<double> { _palletXMinMm + EdgeMarginMm + width / 2 };
            var yValues = new SortedSet<double> { _palletYMinMm + EdgeMarginMm + length / 2 };
            foreach (var item in occupied)
            {
                xValues.Add(item.Left - StackBoxGapMm - width / 2);
                xValues.Add(item.Right + StackBoxGapMm + width / 2);
                yValues.Add(item.Bottom - StackBoxGapMm - length / 2);
                yValues.Add(item.Top + StackBoxGapMm + length / 2);
            }
            foreach (var x in xValues)
            foreach (var y in yValues)
            foreach (var baseZ in CandidateHeights(occupied))
            {
                // 候选必须同时满足边界、碰撞和支撑条件。
                var candidate = new Candidate(x, y, baseZ, width, length, box.HeightMm, orientation);
                if (!InsidePallet(candidate) || Collides(candidate, occupied)) continue;
                var supports = Supporting(candidate, occupied);
                if (baseZ > Epsilon && (supports.Count == 0 || !HasStableSupport(candidate, supports))) continue;
                // 托盘底层为第 0 层，叠放层级取支撑箱的最大层级加一。
                int layer = baseZ <= Epsilon ? 0 : supports.Max(x => x.Placement.LayerIndex) + 1;
                double supportArea = supports.Sum(x =>
                    OverlapLength(candidate.X - candidate.Width / 2, candidate.X + candidate.Width / 2, x.Left, x.Right)
                    * OverlapLength(candidate.Y - candidate.Length / 2, candidate.Y + candidate.Length / 2, x.Bottom, x.Top));
                var placement = new BoxPlacement
                {
                    Order = box.Order,
                    BoxNumber = box.BoxNumber,
                    Xmm = Round(candidate.X),
                    Ymm = Round(candidate.Y),
                    Zmm = Round(PlaceFloorZMm - candidate.BaseZ - box.HeightMm),
                    OrientationDeg = orientation,
                    LayerIndex = layer,
                };
                candidates.Add(new CandidatePlacement(placement, candidate with { Layer = layer }, supports.Count, supportArea));
            }
        }
        // 使用完整且固定的决胜顺序，不能依赖候选首次遍历顺序。
        return candidates.OrderBy(x => x.Placement.LayerIndex)
            .ThenBy(x => x.Candidate.BaseZ)
            .ThenByDescending(x => x.SupportArea)
            .ThenByDescending(x => x.SupportCount)
            .ThenBy(x => x.Placement.Ymm)
            .ThenBy(x => x.Placement.Xmm)
            .ThenBy(x => x.Placement.OrientationDeg)
            .ThenBy(x => x.Placement.BoxNumber, StringComparer.Ordinal)
            .Take(MaxCandidatesPerBox)
            .ToArray();
    }

    /// <summary>
    /// 返回允许尝试的箱底高度：托盘底面和所有已有箱子的顶部。
    /// 这样候选箱只能落在托盘或同高支撑面上，不会产生悬空位置。
    /// </summary>
    private static IEnumerable<double> CandidateHeights(IReadOnlyList<PlacedBox> occupied) =>
        new[] { 0d }.Concat(occupied.Select(x => x.BaseZ + x.Height)).Distinct().OrderBy(x => x);

    private static List<PlacedBox> Supporting(Candidate c, IReadOnlyList<PlacedBox> occupied)
    {
        // 只有 CanSupport=true 的箱顶才可作为规划支撑面。
        // 非 OnShelf 箱子虽然不一定是真实成功占用，但其规划占用可以提供支撑。
        return occupied.Where(x => x.TopZ <= c.BaseZ + Epsilon && Math.Abs(x.TopZ - c.BaseZ) <= Epsilon
                && x.CanSupport
                && OverlapLength(c.X - c.Width / 2, c.X + c.Width / 2, x.Left, x.Right) > Epsilon
                && OverlapLength(c.Y - c.Length / 2, c.Y + c.Length / 2, x.Bottom, x.Top) > Epsilon).ToList();
    }

    /// <summary>
    /// 判断箱子重心是否落在至少一个实际支撑箱的重叠区域内。
    /// 仅有边角或边缘交集虽然满足几何不碰撞，但在物理仿真中会产生倾倒。
    /// </summary>
    private static bool HasStableSupport(Candidate candidate, IReadOnlyList<PlacedBox> supports)
    {
        const double StabilityMarginMm = 5;
        double totalSupportArea = 0;
        double supportLeft = double.PositiveInfinity;
        double supportRight = double.NegativeInfinity;
        double supportBottom = double.PositiveInfinity;
        double supportTop = double.NegativeInfinity;
        foreach (var support in supports)
        {
            double overlapLeft = Math.Max(candidate.X - candidate.Width / 2, support.Left);
            double overlapRight = Math.Min(candidate.X + candidate.Width / 2, support.Right);
            double overlapBottom = Math.Max(candidate.Y - candidate.Length / 2, support.Bottom);
            double overlapTop = Math.Min(candidate.Y + candidate.Length / 2, support.Top);
            double overlapArea = Math.Max(0, overlapRight - overlapLeft)
                * Math.Max(0, overlapTop - overlapBottom);
            totalSupportArea += overlapArea;
            supportLeft = Math.Min(supportLeft, overlapLeft);
            supportRight = Math.Max(supportRight, overlapRight);
            supportBottom = Math.Min(supportBottom, overlapBottom);
            supportTop = Math.Max(supportTop, overlapTop);
            if (candidate.X >= overlapLeft + StabilityMarginMm
                && candidate.X <= overlapRight - StabilityMarginMm
                && candidate.Y >= overlapBottom + StabilityMarginMm
                && candidate.Y <= overlapTop - StabilityMarginMm)
                return true;
        }

        // 大箱子可能跨越下层箱子的间隙；此时重心不一定落在单个箱子内，
        // 但只要多个支撑箱的合计支撑面积充足且覆盖重心区域，仍属于稳定支撑。
        double candidateArea = candidate.Width * candidate.Length;
        if (totalSupportArea >= candidateArea * 0.5
            && candidate.X >= supportLeft + StabilityMarginMm
            && candidate.X <= supportRight - StabilityMarginMm
            && candidate.Y >= supportBottom + StabilityMarginMm
            && candidate.Y <= supportTop - StabilityMarginMm)
            return true;

        return false;
    }

    /// <summary>检查放置项是否在托盘边界内，并且没有与已有占用发生碰撞。</summary>
    private bool Fits(PlacedBox item, IReadOnlyList<PlacedBox> occupied) => InsidePallet(item) && !Collides(item, occupied);
    private bool InsidePallet(Candidate x) => x.X - x.Width / 2 >= _palletXMinMm - Epsilon && x.X + x.Width / 2 <= _palletXMaxMm + Epsilon
        && x.Y - x.Length / 2 >= _palletYMinMm - Epsilon && x.Y + x.Length / 2 <= _palletYMaxMm + Epsilon
        && x.BaseZ >= -Epsilon && x.BaseZ + x.Height <= StackMaxHeightMm + Epsilon;
    private bool InsidePallet(PlacedBox x) => x.Left >= _palletXMinMm - Epsilon && x.Right <= _palletXMaxMm + Epsilon
        && x.Bottom >= _palletYMinMm - Epsilon && x.Top <= _palletYMaxMm + Epsilon && x.BaseZ >= -Epsilon
        && x.TopZ <= StackMaxHeightMm + Epsilon;
    // 候选箱的二维投影按间隙膨胀；只有 Z 方向存在重叠时才算三维碰撞。
    private static bool Collides(Candidate c, IReadOnlyList<PlacedBox> occupied) => occupied.Any(x =>
        c.BaseZ < x.TopZ - Epsilon && c.BaseZ + c.Height > x.BaseZ + Epsilon
        && OverlapLength(c.X - c.Width / 2 - StackBoxGapMm / 2, c.X + c.Width / 2 + StackBoxGapMm / 2, x.Left - StackBoxGapMm / 2, x.Right + StackBoxGapMm / 2) > Epsilon
        && OverlapLength(c.Y - c.Length / 2 - StackBoxGapMm / 2, c.Y + c.Length / 2 + StackBoxGapMm / 2, x.Bottom - StackBoxGapMm / 2, x.Top + StackBoxGapMm / 2) > Epsilon);
    // 恢复旧方案时使用同一套碰撞规则，保证规划占用与新候选的判断一致。
    private static bool Collides(PlacedBox c, IReadOnlyList<PlacedBox> occupied) => occupied.Any(x =>
        c.BaseZ < x.TopZ - Epsilon && c.TopZ > x.BaseZ + Epsilon
        && OverlapLength(c.Left - StackBoxGapMm / 2, c.Right + StackBoxGapMm / 2, x.Left - StackBoxGapMm / 2, x.Right + StackBoxGapMm / 2) > Epsilon
        && OverlapLength(c.Bottom - StackBoxGapMm / 2, c.Top + StackBoxGapMm / 2, x.Bottom - StackBoxGapMm / 2, x.Top + StackBoxGapMm / 2) > Epsilon);
    private static double OverlapLength(double a1, double a2, double b1, double b2) => Math.Max(0, Math.Min(a2, b2) - Math.Max(a1, b1));
    private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    private static IEnumerable<string[]> TypeCombinations(IReadOnlyList<string> keys, int maxSize)
    {
        int limit = Math.Min(maxSize, keys.Count);
        for (int size = 1; size <= limit; size++)
        {
            foreach (var combination in BuildTypeCombinations(keys, size, 0, new List<string>()))
                yield return combination;
        }
    }

    private static IEnumerable<string[]> BuildTypeCombinations(
        IReadOnlyList<string> keys, int size, int start, List<string> current)
    {
        if (current.Count == size)
        {
            yield return current.ToArray();
            yield break;
        }

        for (int index = start; index <= keys.Count - (size - current.Count); index++)
        {
            current.Add(keys[index]);
            foreach (var combination in BuildTypeCombinations(keys, size, index + 1, current))
                yield return combination;
            current.RemoveAt(current.Count - 1);
        }
    }

    private static IEnumerable<BoxDimension[]> TypeCombinations(
        IReadOnlyList<BoxDimension> dimensions, int maxSize)
    {
        int limit = Math.Min(maxSize, dimensions.Count);
        for (int size = 1; size <= limit; size++)
        {
            foreach (var combination in BuildDimensionCombinations(dimensions, size, 0, new List<BoxDimension>()))
                yield return combination;
        }
    }

    private static IEnumerable<BoxDimension[]> BuildDimensionCombinations(
        IReadOnlyList<BoxDimension> dimensions, int size, int start, List<BoxDimension> current)
    {
        if (current.Count == size)
        {
            yield return current.ToArray();
            yield break;
        }

        for (int index = start; index <= dimensions.Count - (size - current.Count); index++)
        {
            current.Add(dimensions[index]);
            foreach (var combination in BuildDimensionCombinations(dimensions, size, index + 1, current))
                yield return combination;
            current.RemoveAt(current.Count - 1);
        }
    }

    private static string SameSpecTypeId(Box box)
        => SameSpecTypeId(box.LengthMm, box.WidthMm, box.HeightMm);

    private static string SameSpecTypeId(InventoryBoxRecord box)
        // 库存维度语义：Depth->规划Length，Length->规划Width，Height->规划Height。
        => SameSpecTypeId(box.DepthMm, box.LengthMm, box.HeightMm);

    private static string SameSpecTypeId(double lengthMm, double widthMm, double heightMm)
        => BoxTypeClassifier.ClassifyPlanBoxTypeId(lengthMm, widthMm, heightMm);

    private static string BoxTypeKey(Box box)
    {
        double first = Math.Min(box.LengthMm, box.WidthMm);
        double second = Math.Max(box.LengthMm, box.WidthMm);
        return string.Join("x", first.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            second.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            box.HeightMm.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string BoxTypeKey(PlacedBox box)
    {
        double first = Math.Min(box.Length, box.Width);
        double second = Math.Max(box.Length, box.Width);
        return string.Join("x", first.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            second.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            box.Height.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static double BoxBaseArea(Box box) => box.LengthMm * box.WidthMm;
    /// <summary>
    /// 将公开的机械 Z 坐标反算为内部箱底高度 BaseZ，供边界、碰撞和支撑计算使用。
    /// </summary>
    private static PlacedBox ToPlacedBox(Box box, BoxPlacement placement, bool real)
    {
        bool rotated = placement.OrientationDeg == 90;
        double width = rotated ? box.LengthMm : box.WidthMm;
        double length = rotated ? box.WidthMm : box.LengthMm;
        double baseZ = PlaceFloorZMm - placement.Zmm - box.HeightMm;
        // 规划阶段已经写入 occupied 的箱子都可以作为后续候选的支撑面，
        // 包括仍处于 OnShelf 状态但已经被本次方案放置的箱子。
        // real 仅表示是否为真实成功占用，不限制规划支撑能力。
        return new PlacedBox(placement.Xmm, placement.Ymm, baseZ, width, length, box.HeightMm, placement,
            real, true);
    }

    /// <summary>按唯一箱号查找内部箱子对象。</summary>
    private Box? FindBox(string number) => _group.MutableBoxes.FirstOrDefault(x => x.BoxNumber == number);

    private static bool TryNormalizeSucceededPlacement(
        SucceededPlacementRecord? input,
        out SucceededPlacementRecord normalized)
    {
        normalized = default!;
        if (input is null || input.Placement is null)
            return false;

        string boxNumber = input.BoxNumber?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(boxNumber))
            return false;

        if (!double.IsFinite(input.LengthMm) || input.LengthMm <= 0
            || !double.IsFinite(input.WidthMm) || input.WidthMm <= 0
            || !double.IsFinite(input.HeightMm) || input.HeightMm <= 0
            || !double.IsFinite(input.WeightKg) || input.WeightKg < 0)
            return false;

        string placementBoxNumber = input.Placement.BoxNumber?.Trim() ?? string.Empty;
        if (!string.Equals(boxNumber, placementBoxNumber, StringComparison.Ordinal))
            return false;

        if (!double.IsFinite(input.Placement.Xmm)
            || !double.IsFinite(input.Placement.Ymm)
            || !double.IsFinite(input.Placement.Zmm)
            || input.Placement.Order < 0
            || input.Placement.LayerIndex < 0
            || input.Placement.OrientationDeg is not (0 or 90))
            return false;

        normalized = new SucceededPlacementRecord
        {
            BoxNumber = boxNumber,
            LengthMm = input.LengthMm,
            WidthMm = input.WidthMm,
            HeightMm = input.HeightMm,
            WeightKg = input.WeightKg,
            Placement = new BoxPlacement
            {
                Order = input.Placement.Order,
                BoxNumber = boxNumber,
                Xmm = input.Placement.Xmm,
                Ymm = input.Placement.Ymm,
                Zmm = input.Placement.Zmm,
                OrientationDeg = input.Placement.OrientationDeg,
                LayerIndex = input.Placement.LayerIndex,
            },
        };
        return true;
    }

    /// <summary>
    /// 校验成功堆垛集合。实际已发生的成功落点不因超出托盘或间距不足而拒绝写入，
    /// 仅拒绝 Order 重复；几何问题以警告文本返回（message 为空表示无警告）。
    /// </summary>
    private bool ValidateSucceededSet(
        IEnumerable<SucceededPlacementRecord> records,
        out string message)
    {
        var occupied = new List<PlacedBox>();
        var definitionsByTypeId = GetBoxTypeDefinitions()
            .ToDictionary(item => item.TypeId, item => item, StringComparer.Ordinal);
        var orders = new HashSet<int>();
        var warnings = new List<string>();
        foreach (var item in records
                     .OrderBy(x => x.Placement.Order)
                     .ThenBy(x => x.BoxNumber, StringComparer.Ordinal))
        {
            if (!orders.Add(item.Placement.Order))
            {
                message = $"成功堆垛数据中存在重复 Order：{item.Placement.Order}。";
                return false;
            }

            var box = new Box
            {
                BoxNumber = item.BoxNumber,
                LengthMm = item.LengthMm,
                WidthMm = item.WidthMm,
                HeightMm = item.HeightMm,
                WeightKg = item.WeightKg,
                Status = BoxStatus.StackingSucceeded,
                Order = item.Placement.Order,
            };
            var collisionBox = ToCollisionSizedBox(box, definitionsByTypeId);
            var placed = ToPlacedBox(collisionBox, item.Placement, real: true);
            if (!InsidePallet(placed))
                warnings.Add($"箱子 {item.BoxNumber} 的成功落点超出托盘范围");

            if (Collides(placed, occupied))
                warnings.Add($"箱子 {item.BoxNumber} 的成功落点与已有成功占位间距不足或碰撞");

            occupied.Add(placed);
        }

        message = warnings.Count == 0 ? string.Empty : "警告：" + string.Join("；", warnings) + "。";
        return true;
    }

    /// <summary>校验箱号、尺寸和重量，阻止无效几何进入规划器。</summary>
    private static void ValidateBox(Box box)
    {
        if (string.IsNullOrWhiteSpace(box.BoxNumber)) throw new ArgumentException("箱号不能为空。", nameof(box));
        if (box.LengthMm <= 0 || box.WidthMm <= 0 || box.HeightMm <= 0) throw new ArgumentException("箱子尺寸必须大于 0。", nameof(box));
        if (box.WeightKg < 0) throw new ArgumentException("箱子重量不能小于 0。", nameof(box));
    }
    private static Box CloneBox(Box x) => new() { BoxNumber = x.BoxNumber, WeightKg = x.WeightKg, LengthMm = x.LengthMm, WidthMm = x.WidthMm, HeightMm = x.HeightMm, Status = x.Status, Order = x.Order };
    private static BoxPlacement ClonePlacement(BoxPlacement x) => x with { };
    private static StackPlanResult ClonePlan(StackPlanResult x) => new() { PlanningResult = x.PlanningResult, Placements = x.Placements.Select(ClonePlacement).ToArray() };

    /// <summary>已堆垛成功箱子的固定位置和尺寸快照。</summary>
    private sealed record FixedPlacement(BoxPlacement Placement, double Length, double Width, double Height);
    /// <summary>继续规划使用的成功堆垛基线。</summary>
    private sealed record SucceededBaselineState(IReadOnlyList<Box> Boxes, IReadOnlyList<BoxPlacement> Placements);
    /// <summary>成功层补齐计划：补齐数量与后续需要剔除的候选尺寸。</summary>
    private sealed record SucceededLayerFillPlan(string TypeId, BoxDimension Dimension, int NeededCount);

    /// <summary>
    /// 规划器内部的三维占用。IsReal 表示真实成功占用，CanSupport 表示其顶部能否支撑后续箱子。
    /// </summary>
    private sealed record PlacedBox(double X, double Y, double BaseZ, double Width, double Length, double Height,
        BoxPlacement Placement, bool IsReal, bool CanSupport)
    {
        public double Left => X - Width / 2;
        public double Right => X + Width / 2;
        public double Bottom => Y - Length / 2;
        public double Top => Y + Length / 2;
        public double TopZ => BaseZ + Height;
    }
    /// <summary>待筛选的候选位置及其确定性排序所需的评分信息。</summary>
    private sealed record Candidate(double X, double Y, double BaseZ, double Width, double Length, double Height,
        int Orientation, int Layer = 0);
    /// <summary>单个箱子的合法候选位置及其内部评分。</summary>
    private sealed record CandidatePlacement(BoxPlacement Placement, Candidate Candidate, int SupportCount, double SupportArea);
    /// <summary>有限宽度搜索的一层布局状态。</summary>
    private sealed record SearchState(IReadOnlyList<Box> Remaining, IReadOnlyList<PlacedBox> Occupied,
        IReadOnlyDictionary<string, BoxPlacement> Placements);

    /// <summary>直接二维单层搜索中的一个箱子放置结果。</summary>
    private sealed record DirectLayerPlacement(
        BoxDimension Dimension,
        double Xmm,
        double Ymm,
        double Width,
        double Length,
        int OrientationDeg)
    {
        public double Left => Xmm - Width / 2;
        public double Right => Xmm + Width / 2;
        public double Bottom => Ymm - Length / 2;
        public double Top => Ymm + Length / 2;
    }
    /// <summary>直接二维单层搜索状态。</summary>
    private sealed record DirectLayerState(
        IReadOnlyList<DirectLayerPlacement> Placements,
        double OccupiedArea);
    /// <summary>一个确定性的单层布局候选。</summary>
    private sealed record DirectLayerResult(
        double HeightMm,
        IReadOnlyList<DirectLayerPlacement> Placements,
        double OccupiedArea,
        string Signature);
    /// <summary>利用率规划内部箱子模型：保留原始箱号与顺序，尺寸使用类别代表值。</summary>
    private sealed record UtilizationPlanBox(
        Box Source,
        string TypeId,
        double PlanLengthMm,
        double PlanWidthMm,
        double PlanHeightMm,
        string TypeKey);
    /// <summary>有限真实箱子的一层二维放置结果。</summary>
    private sealed record DirectBoxLayerPlacement(
        UtilizationPlanBox Box,
        double Xmm,
        double Ymm,
        double Width,
        double Length,
        int OrientationDeg)
    {
        public double Left => Xmm - Width / 2;
        public double Right => Xmm + Width / 2;
        public double Bottom => Ymm - Length / 2;
        public double Top => Ymm + Length / 2;
    }
    /// <summary>有限真实箱子的一层二维搜索状态。</summary>
    private sealed record DirectBoxLayerState(
        IReadOnlyList<DirectBoxLayerPlacement> Placements,
        double OccupiedArea,
        IReadOnlyList<UtilizationPlanBox> Remaining);
    /// <summary>绑定真实箱子的单层二维布局候选。</summary>
    private sealed record DirectBoxLayerResult(
        double HeightMm,
        IReadOnlyList<DirectBoxLayerPlacement> Placements,
        double OccupiedArea,
        string Signature);
    /// <summary>不同高度单层方案组合后的整垛结果。</summary>
    private sealed record DirectStackResult(
        IReadOnlyList<DirectLayerResult> Layers,
        double TotalArea,
        int TotalCount,
        string Signature);
}
