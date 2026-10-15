namespace CoreDllFunctionTester.Simulation;

/// <summary>
/// 仿真数据源：提供待仿真场景。由适配器实现，引擎不关心数据来自哪里。
/// </summary>
public interface ISimulationDataSource
{
    /// <summary>读取仿真场景。</summary>
    SimulationScene LoadScene();
}

/// <summary>
/// 仿真状态回写接口：每个箱子状态变化时调用，由适配器写回业务系统。
/// </summary>
public interface ISimulationStatusSink
{
    /// <summary>回写箱子状态。</summary>
    void SetState(SimulationBoxItem box, SimulationBoxState state);
}

/// <summary>
/// 仿真控制接口：后端在动作的安全点调用，用于响应暂停。
/// </summary>
public interface ISimulationControl
{
    /// <summary>若处于暂停状态则等待恢复；收到取消请求时抛出 OperationCanceledException。</summary>
    Task WaitIfPausedAsync(CancellationToken ct);
}

/// <summary>
/// 仿真后端：负责单个箱子的运动过程。
/// 当前实现为运动学回放（无物理）；后续接入 URDF 机械臂时，新增一个实现即可。
/// </summary>
public interface ISimulationBackend
{
    /// <summary>后端名称，用于日志展示。</summary>
    string Name { get; }

    /// <summary>执行单个箱子的堆垛动作，过程中通过 onPose 报告位姿。</summary>
    Task ExecuteAsync(
        SimulationBoxItem box,
        ISimulationControl control,
        Action<SimulationPose> onPose,
        CancellationToken ct);
}
