namespace CoreDllFunctionTester.Simulation;

/// <summary>
/// 仿真箱子状态。与 Core 的 BoxStatus 解耦，由适配器负责映射。
/// </summary>
public enum SimulationBoxState
{
    /// <summary>待堆垛。</summary>
    Pending,
    /// <summary>堆垛中。</summary>
    Stacking,
    /// <summary>堆垛成功。</summary>
    Succeeded,
    /// <summary>堆垛失败。</summary>
    Failed,
}

/// <summary>
/// 单个箱子的仿真输入。坐标口径与 Core 规划结果一致：
/// X/Y 为箱中心（托盘坐标，mm），BottomZMm 为箱底高度（距托盘底面，mm）。
/// </summary>
public sealed record SimulationBoxItem
{
    /// <summary>箱号。</summary>
    public required string BoxNumber { get; init; }

    /// <summary>堆垛顺序。</summary>
    public required int Order { get; init; }

    /// <summary>箱长（mm）。</summary>
    public required double LengthMm { get; init; }

    /// <summary>箱宽（mm）。</summary>
    public required double WidthMm { get; init; }

    /// <summary>箱高（mm）。</summary>
    public required double HeightMm { get; init; }

    /// <summary>重量（kg）。</summary>
    public required double WeightKg { get; init; }

    /// <summary>箱中心 X（mm）。</summary>
    public required double XMm { get; init; }

    /// <summary>箱中心 Y（mm）。</summary>
    public required double YMm { get; init; }

    /// <summary>箱底高度（mm，距托盘底面）。</summary>
    public required double BottomZMm { get; init; }

    /// <summary>朝向（0 或 90）。</summary>
    public required int OrientationDeg { get; init; }

    /// <summary>层号（从 0 开始）。</summary>
    public required int LayerIndex { get; init; }

    /// <summary>数据源中该箱是否已经是堆垛成功状态。</summary>
    public required bool IsSucceeded { get; init; }

    /// <summary>占地 X 方向尺寸（mm），与 Core 的 ToPlacedBox 口径一致。</summary>
    public double FootprintXMm => OrientationDeg == 90 ? LengthMm : WidthMm;

    /// <summary>占地 Y 方向尺寸（mm），与 Core 的 ToPlacedBox 口径一致。</summary>
    public double FootprintYMm => OrientationDeg == 90 ? WidthMm : LengthMm;
}

/// <summary>
/// 仿真场景：托盘范围与待仿真箱子列表。
/// </summary>
public sealed record SimulationScene
{
    /// <summary>托盘 X 最小值（mm）。</summary>
    public required double PalletXMinMm { get; init; }

    /// <summary>托盘 X 最大值（mm）。</summary>
    public required double PalletXMaxMm { get; init; }

    /// <summary>托盘 Y 最小值（mm）。</summary>
    public required double PalletYMinMm { get; init; }

    /// <summary>托盘 Y 最大值（mm）。</summary>
    public required double PalletYMaxMm { get; init; }

    /// <summary>最大堆垛高度（mm），用于可视化比例。</summary>
    public required double StackMaxHeightMm { get; init; }

    /// <summary>已规划的箱子，按堆垛顺序排列。</summary>
    public required IReadOnlyList<SimulationBoxItem> Boxes { get; init; }
}

/// <summary>
/// 后端输出的箱子位姿，供可视化使用。
/// </summary>
/// <param name="Box">箱子信息。</param>
/// <param name="XMm">箱中心 X（mm）。</param>
/// <param name="YMm">箱中心 Y（mm）。</param>
/// <param name="BottomZMm">箱底高度（mm）。</param>
public sealed record SimulationPose(SimulationBoxItem Box, double XMm, double YMm, double BottomZMm);

/// <summary>
/// 仿真事件类型。
/// </summary>
public enum SimulationEventKind
{
    /// <summary>普通信息。</summary>
    Info,
    /// <summary>异常。</summary>
    Error,
}

/// <summary>
/// 仿真事件，用于界面日志展示。
/// </summary>
/// <param name="TimeLocal">事件时间。</param>
/// <param name="Kind">事件类型。</param>
/// <param name="BoxNumber">相关箱号，无关时为空字符串。</param>
/// <param name="Message">事件描述。</param>
public sealed record SimulationEvent(DateTime TimeLocal, SimulationEventKind Kind, string BoxNumber, string Message);

/// <summary>
/// 仿真参数。
/// </summary>
public sealed record SimulationOptions
{
    /// <summary>每个箱子堆垛完成后的停顿（毫秒）。</summary>
    public int BoxIntervalMs { get; init; } = 500;

    /// <summary>单箱运动的插值步数。</summary>
    public int MotionSteps { get; init; } = 20;

    /// <summary>每步之间的延时（毫秒）。</summary>
    public int StepDelayMs { get; init; } = 30;

    /// <summary>释放高度（mm），与 Mujuco 的释放高度 0.04 m 一致。</summary>
    public double ReleaseHeightMm { get; init; } = 40;
}
