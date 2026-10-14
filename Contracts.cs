namespace StackPlanner.Core;

/// <summary>
/// 箱子在供包、运输和堆垛流程中的业务状态。
/// </summary>
public enum BoxStatus
{
    /// <summary>箱子仍在货架上，可以参与顺序重新规划。</summary>
    OnShelf,
    /// <summary>箱子已经从货架取下，但尚未到达堆垛位置。</summary>
    RemovedFromShelf,
    /// <summary>箱子已经到达堆垛工位，等待机械执行。</summary>
    Arrived,
    /// <summary>机械正在执行该箱子的堆垛动作。</summary>
    Stacking,
    /// <summary>箱子已经实际放置成功，形成托盘上的真实固定占用。</summary>
    StackingSucceeded,
    /// <summary>箱子的本次堆垛动作失败，箱子仍保留在集合中等待业务处理。</summary>
    StackingFailed,
}

/// <summary>
/// 独立箱子的输入属性和由 DLL 维护的业务规划状态。
/// 每个实例代表一个真实箱子，不与其他箱子合并数量。
/// </summary>
public sealed record Box
{
    /// <summary>箱子的唯一业务编号；同一个 <see cref="BoxGroup" /> 内不能重复。</summary>
    public required string BoxNumber { get; init; }
    /// <summary>箱子重量，单位为 kg；当前版本用于输入校验，重量约束后续接入。</summary>
    public double WeightKg { get; init; }
    /// <summary>箱子长度，单位为 mm。</summary>
    public double LengthMm { get; init; }
    /// <summary>箱子宽度，单位为 mm。</summary>
    public double WidthMm { get; init; }
    /// <summary>箱子高度，单位为 mm。</summary>
    public double HeightMm { get; init; }
    /// <summary>当前业务状态；新箱子默认为 <see cref="BoxStatus.OnShelf" />。</summary>
    public BoxStatus Status { get; set; } = BoxStatus.OnShelf;
    /// <summary>
    /// 当前规划中的堆垛顺序，从 0 开始；-1 表示尚未分配。
    /// 正常规划完成后，该值等于规划结果 Placements 数组中对应项的下标。
    /// <see cref="BoxStatus.Stacking" /> 和 <see cref="BoxStatus.StackingSucceeded" />
    /// 箱子的顺序视为已锁定；其他未锁定箱子可在重新规划时调整。
    /// </summary>
    public int Order { get; set; } = -1;
}

/// <summary>
/// 当前任务中的独立箱子集合。
/// 集合本身不按照箱型合并，也不保存规划器之外的设备状态。
/// </summary>
public sealed class BoxGroup
{
    private readonly List<Box> _boxes = new();
    /// <summary>以只读视图提供当前箱子，防止调用方直接替换集合。</summary>
    public IReadOnlyList<Box> Boxes => _boxes;
    /// <summary>供 <see cref="StackPlanner" /> 内部执行增删改的可变集合。</summary>
    internal List<Box> MutableBoxes => _boxes;
}

/// <summary>
/// 单个箱子的堆垛规划结果。
/// 坐标为毫米，位置使用箱体中心点，朝向只允许 0° 或 90°。
/// </summary>
public sealed record BoxPlacement
{
    /// <summary>该箱子在所属规划结果 Placements 数组中的下标，从 0 开始。</summary>
    public required int Order { get; init; }
    /// <summary>该位置对应的唯一箱号。</summary>
    public required string BoxNumber { get; init; }
    /// <summary>箱体中心在托盘坐标系中的 X 坐标，单位为 mm。</summary>
    public required double Xmm { get; init; }
    /// <summary>箱体中心在托盘坐标系中的 Y 坐标，单位为 mm。</summary>
    public required double Ymm { get; init; }
    /// <summary>机械执行使用的 Z 坐标，单位为 mm。</summary>
    public required double Zmm { get; init; }
    /// <summary>箱子的平面朝向，只能是 0 或 90。</summary>
    public required int OrientationDeg { get; init; }
    /// <summary>从托盘底面开始计算的层号，底层为 0。</summary>
    public required int LayerIndex { get; init; }
}

/// <summary>
/// 一次完整规划的公开结果。
/// 结果只包含应用层执行所需的放置项和整体成功标志。
/// </summary>
public sealed class StackPlanResult
{
    /// <summary>按规划顺序排列的箱子放置结果。</summary>
    public required IReadOnlyList<BoxPlacement> Placements { get; init; }
    /// <summary>所有箱子都成功获得合法位置时为 true，否则为 false。</summary>
    public required bool PlanningResult { get; init; }
}

/// <summary>
/// Core 对外发布的完整状态快照，用于跨进程显示和状态恢复。
/// </summary>
public sealed class PlannerSnapshot
{
    /// <summary>快照文件结构版本。</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>同一快照文件内单调递增的版本号。</summary>
    public required long Revision { get; init; }
    /// <summary>快照发布时间（UTC）。</summary>
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    /// <summary>Core 当前维护的完整箱子集合。</summary>
    public required IReadOnlyList<Box> Boxes { get; init; }
    /// <summary>Core 当前维护的规划结果。</summary>
    public required StackPlanResult Plan { get; init; }
    /// <summary>利用率规划删除箱子后保留的可复用位置。</summary>
    public required IReadOnlyList<ReusablePlacementSet> ReusablePlacements { get; init; }
}

/// <summary>
/// 面向 DLL 消费方的逐箱明细视图。
/// 字段全部由 Core 生成，不依赖调用方自行拼装。
/// </summary>
public sealed class PlannerBoxDetail
{
    /// <summary>堆垛顺序；未分配时可能为 -1。</summary>
    public required int Order { get; init; }
    /// <summary>唯一箱号。</summary>
    public required string BoxNumber { get; init; }
    /// <summary>箱型类别（TypeId）。</summary>
    public required string TypeId { get; init; }
    /// <summary>箱子长度，单位为 mm。</summary>
    public required double LengthMm { get; init; }
    /// <summary>箱子宽度，单位为 mm。</summary>
    public required double WidthMm { get; init; }
    /// <summary>箱子高度，单位为 mm。</summary>
    public required double HeightMm { get; init; }
    /// <summary>箱子重量，单位为 kg。</summary>
    public required double WeightKg { get; init; }
    /// <summary>箱子当前业务状态。</summary>
    public required BoxStatus Status { get; init; }
    /// <summary>该箱子是否已有当前规划放置点。</summary>
    public required bool Planned { get; init; }
    /// <summary>规划点 X，单位为 mm；未规划时为空。</summary>
    public double? Xmm { get; init; }
    /// <summary>规划点 Y，单位为 mm；未规划时为空。</summary>
    public double? Ymm { get; init; }
    /// <summary>规划点 Z，单位为 mm；未规划时为空。</summary>
    public double? Zmm { get; init; }
    /// <summary>规划方向（0 或 90）；未规划时为空。</summary>
    public int? OrientationDeg { get; init; }
    /// <summary>规划层数；未规划时为空。</summary>
    public int? LayerIndex { get; init; }
}

/// <summary>
/// Core 对外提供的逐箱明细结果，用于接口联调与外部系统验证。
/// </summary>
public sealed class PlannerBoxDetailResult
{
    /// <summary>分类容差，单位为 mm。</summary>
    public required double ToleranceMm { get; init; }
    /// <summary>当前规划整体是否成功。</summary>
    public required bool PlanningResult { get; init; }
    /// <summary>当前箱子总数。</summary>
    public required int TotalBoxes { get; init; }
    /// <summary>当前已有规划放置点的箱子数量。</summary>
    public required int PlannedBoxes { get; init; }
    /// <summary>逐箱明细。</summary>
    public required IReadOnlyList<PlannerBoxDetail> Boxes { get; init; }
    /// <summary>结果生成时间（UTC）。</summary>
    public required DateTimeOffset GeneratedAtUtc { get; init; }
}

/// <summary>按完整尺寸归类的可复用规划位置。</summary>
public sealed class ReusablePlacementSet
{
    /// <summary>可复用位置所属的箱子完整尺寸。</summary>
    public required BoxDimension Dimension { get; init; }
    /// <summary>按规划顺序排列的可复用位置。</summary>
    public required IReadOnlyList<BoxPlacement> Placements { get; init; }
}

/// <summary>利用率规划加减箱操作结果。</summary>
public sealed class UtilizationMutationResult
{
    /// <summary>本次操作是否成功。</summary>
    public required bool Succeeded { get; init; }
    /// <summary>Core 返回的操作说明。</summary>
    public required string Message { get; init; }
    /// <summary>操作完成后的箱子集合。</summary>
    public required IReadOnlyList<Box> Boxes { get; init; }
    /// <summary>操作完成后的规划快照。</summary>
    public required StackPlanResult Plan { get; init; }
    /// <summary>操作完成后仍可复用的规划位置。</summary>
    public required IReadOnlyList<ReusablePlacementSet> ReusablePlacements { get; init; }
}

/// <summary>仅由箱体尺寸描述的候选箱型。</summary>
public sealed record BoxDimension
{
    /// <summary>箱子长度，单位为 mm。</summary>
    public required double LengthMm { get; init; }
    /// <summary>箱子宽度，单位为 mm。</summary>
    public required double WidthMm { get; init; }
    /// <summary>箱子高度，单位为 mm。</summary>
    public required double HeightMm { get; init; }
    /// <summary>整垛规划中该箱型允许使用的最小数量。</summary>
    public int MinimumCount { get; init; }
}

/// <summary>单层空间利用率摘要。</summary>
public sealed class LayerUtilizationSummary
{
    /// <summary>层号，从 0 开始。</summary>
    public required int LayerIndex { get; init; }
    /// <summary>本层底面积利用率。</summary>
    public required double Utilization { get; init; }
}

/// <summary>一种箱型在最佳方案中的实际数量。</summary>
public sealed class BoxTypeCount
{
    /// <summary>箱子长度，单位为 mm。</summary>
    public required double LengthMm { get; init; }
    /// <summary>箱子宽度，单位为 mm。</summary>
    public required double WidthMm { get; init; }
    /// <summary>箱子高度，单位为 mm。</summary>
    public required double HeightMm { get; init; }
    /// <summary>该箱型的规划数量。</summary>
    public required int Count { get; init; }
}

/// <summary>仅按箱体尺寸生成的空间利用率最优规划结果。</summary>
public sealed class UtilizationPlanResult
{
    /// <summary>各箱型在最佳方案中的实际数量。</summary>
    public required IReadOnlyList<BoxTypeCount> PlannedCounts { get; init; }
    /// <summary>逐层空间利用率。</summary>
    public required IReadOnlyList<LayerUtilizationSummary> Layers { get; init; }
    /// <summary>整垛各层利用率的平均值。</summary>
    public required double Utilization { get; init; }
    /// <summary>所有箱型的最小数量约束均满足时为 true。</summary>
    public bool PlanningResult { get; init; } = true;
    /// <summary>规划失败时的中文原因；成功时为空字符串。</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 基于库存的利用率规划结果。
/// 由 Core 直接返回，包含策略统计与是否已应用到 Core 内存。
/// </summary>
public sealed class InventoryUtilizationPlanResult
{
    /// <summary>空间利用率规划是否成功。</summary>
    public required bool PlanningResult { get; init; }
    /// <summary>结果说明；失败时为中文原因。</summary>
    public required string Message { get; init; }
    /// <summary>各箱型在最佳方案中的实际数量。</summary>
    public required IReadOnlyList<BoxTypeCount> PlannedCounts { get; init; }
    /// <summary>逐层空间利用率。</summary>
    public required IReadOnlyList<LayerUtilizationSummary> Layers { get; init; }
    /// <summary>整垛各层利用率的平均值。</summary>
    public required double Utilization { get; init; }
    /// <summary>是否已由 Core 成功将选中箱子写入当前内存快照。</summary>
    public required bool Applied { get; init; }
    /// <summary>本次由 Core 从库存选中的箱子数量。</summary>
    public required int SelectedBoxCount { get; init; }
}

/// <summary>
/// 待堆垛库存中的单箱记录。
/// 尺寸语义固定为 length/height/depth，对应长/高/宽（单位均为 mm）。
/// </summary>
public sealed record InventoryBoxRecord
{
    /// <summary>箱子的唯一业务编号。</summary>
    public required string BoxNumber { get; init; }
    /// <summary>箱子长度，单位为 mm。</summary>
    public required double LengthMm { get; init; }
    /// <summary>箱子高度，单位为 mm。</summary>
    public required double HeightMm { get; init; }
    /// <summary>箱子宽度（depth），单位为 mm。</summary>
    public required double DepthMm { get; init; }
    /// <summary>箱子重量，单位为 kg。</summary>
    public required double WeightKg { get; init; }
    /// <summary>数据来源标识，如 RackBox。</summary>
    public string Source { get; init; } = string.Empty;
    /// <summary>该记录最近一次被写入库存数据库的时间（UTC）。</summary>
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>库存写入结果。</summary>
public sealed class InventoryUpsertResult
{
    /// <summary>本次成功写入（新增+更新）的数量。</summary>
    public required int Upserted { get; init; }
    /// <summary>本次新增数量。</summary>
    public required int Inserted { get; init; }
    /// <summary>本次更新数量。</summary>
    public required int Updated { get; init; }
    /// <summary>本次无效记录数量。</summary>
    public required int Invalid { get; init; }
    /// <summary>库存中当前有效总数。</summary>
    public required int Total { get; init; }
    /// <summary>操作说明。</summary>
    public required string Message { get; init; }
}

/// <summary>Core 配置中的一个箱型定义（来自 stackplanner.config）。</summary>
public sealed class BoxTypeDefinition
{
    /// <summary>箱型编号。</summary>
    public required string TypeId { get; init; }
    /// <summary>代表长度，单位为 mm。</summary>
    public required double RepLengthMm { get; init; }
    /// <summary>代表高度，单位为 mm。</summary>
    public required double RepHeightMm { get; init; }
    /// <summary>代表宽度（depth），单位为 mm。</summary>
    public required double RepDepthMm { get; init; }
}

/// <summary>一个箱型分类簇的摘要。</summary>
public sealed class BoxTypeClusterSummary
{
    /// <summary>类别编号。</summary>
    public required string TypeId { get; init; }
    /// <summary>该类别内箱子数量。</summary>
    public required int Count { get; init; }
    /// <summary>代表长度，单位为 mm。</summary>
    public required double RepLengthMm { get; init; }
    /// <summary>代表高度，单位为 mm。</summary>
    public required double RepHeightMm { get; init; }
    /// <summary>代表宽度（depth），单位为 mm。</summary>
    public required double RepDepthMm { get; init; }
    /// <summary>该类别平均重量，单位为 kg。</summary>
    public required double WeightAvgKg { get; init; }
    /// <summary>该类别最小重量，单位为 kg。</summary>
    public required double WeightMinKg { get; init; }
    /// <summary>该类别最大重量，单位为 kg。</summary>
    public required double WeightMaxKg { get; init; }
}

/// <summary>单个库存箱子的分类结果。</summary>
public sealed class BoxTypeAssignment
{
    /// <summary>箱子的唯一业务编号。</summary>
    public required string BoxNumber { get; init; }
    /// <summary>分类后的类别编号。</summary>
    public required string TypeId { get; init; }
    /// <summary>原始长度，单位为 mm。</summary>
    public required double LengthMm { get; init; }
    /// <summary>原始高度，单位为 mm。</summary>
    public required double HeightMm { get; init; }
    /// <summary>原始宽度（depth），单位为 mm。</summary>
    public required double DepthMm { get; init; }
    /// <summary>原始重量，单位为 kg。</summary>
    public required double WeightKg { get; init; }
    /// <summary>归类基准长度，单位为 mm；未匹配配置类别时为空。</summary>
    public double? BaseLengthMm { get; init; }
    /// <summary>归类基准高度，单位为 mm；未匹配配置类别时为空。</summary>
    public double? BaseHeightMm { get; init; }
    /// <summary>归类基准宽度（depth），单位为 mm；未匹配配置类别时为空。</summary>
    public double? BaseDepthMm { get; init; }
    /// <summary>数据来源标识，如 RackBox。</summary>
    public string Source { get; init; } = string.Empty;
}

/// <summary>库存箱型分类结果。</summary>
public sealed class BoxTypeClassificationResult
{
    /// <summary>分类容差，单位为 mm。</summary>
    public required double ToleranceMm { get; init; }
    /// <summary>参与分类的箱子总数。</summary>
    public required int TotalBoxes { get; init; }
    /// <summary>分类簇数量。</summary>
    public required int ClusterCount { get; init; }
    /// <summary>分类簇明细。</summary>
    public required IReadOnlyList<BoxTypeClusterSummary> Clusters { get; init; }
    /// <summary>每个箱子的分类明细。</summary>
    public required IReadOnlyList<BoxTypeAssignment> Assignments { get; init; }
    /// <summary>结果生成时间（UTC）。</summary>
    public required DateTimeOffset GeneratedAtUtc { get; init; }
}
