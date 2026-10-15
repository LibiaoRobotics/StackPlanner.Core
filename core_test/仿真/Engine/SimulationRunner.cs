namespace CoreDllFunctionTester.Simulation;

/// <summary>
/// 仿真运行器：按顺序逐箱堆垛，负责暂停/继续/停止，并把状态回写到 <see cref="ISimulationStatusSink"/>。
/// 只依赖仿真自身的接口与模型，不引用 Core。
/// </summary>
public sealed class SimulationRunner : ISimulationControl
{
    private readonly ISimulationBackend _backend;
    private readonly ISimulationStatusSink _statusSink;
    private readonly SimulationOptions _options;
    private TaskCompletionSource<bool> _resumeSignal = CreateOpenSignal();

    /// <summary>创建仿真运行器。</summary>
    public SimulationRunner(ISimulationBackend backend, ISimulationStatusSink statusSink, SimulationOptions options)
    {
        _backend = backend;
        _statusSink = statusSink;
        _options = options;
    }

    /// <summary>事件日志。</summary>
    public event Action<SimulationEvent>? EventRaised;

    /// <summary>箱子状态变化（已完成回写后触发）。</summary>
    public event Action<SimulationBoxItem, SimulationBoxState>? BoxStateChanged;

    /// <summary>运动中箱子的位姿变化。</summary>
    public event Action<SimulationPose>? PoseChanged;

    /// <summary>当前是否处于暂停状态。</summary>
    public bool IsPaused { get; private set; }

    /// <summary>暂停仿真。在下一个安全点生效。</summary>
    public void Pause()
    {
        if (IsPaused)
            return;

        IsPaused = true;
        _resumeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Raise(SimulationEventKind.Info, string.Empty, "已暂停");
    }

    /// <summary>继续仿真。</summary>
    public void Resume()
    {
        if (!IsPaused)
            return;

        IsPaused = false;
        _resumeSignal.TrySetResult(true);
        Raise(SimulationEventKind.Info, string.Empty, "已继续");
    }

    /// <inheritdoc />
    public async Task WaitIfPausedAsync(CancellationToken ct)
    {
        if (!IsPaused)
        {
            ct.ThrowIfCancellationRequested();
            return;
        }

        await _resumeSignal.Task.WaitAsync(ct);
    }

    /// <summary>
    /// 按给定顺序逐箱仿真。停止时正在堆垛的箱子回退为待堆垛；异常时标记为堆垛失败并结束。
    /// 该方法不向外抛出异常，结果通过 <see cref="EventRaised"/> 通知。
    /// </summary>
    public async Task RunAsync(IReadOnlyList<SimulationBoxItem> boxes, CancellationToken ct)
    {
        Raise(SimulationEventKind.Info, string.Empty, $"开始仿真，共 {boxes.Count} 个箱子，后端：{_backend.Name}");
        SimulationBoxItem? current = null;

        try
        {
            foreach (var box in boxes)
            {
                current = box;
                await WaitIfPausedAsync(ct);
                ChangeState(box, SimulationBoxState.Stacking, "堆垛中");

                await _backend.ExecuteAsync(box, this, pose => PoseChanged?.Invoke(pose), ct);

                ChangeState(box, SimulationBoxState.Succeeded, "堆垛成功");
                current = null;

                await Task.Delay(_options.BoxIntervalMs, ct);
            }

            Raise(SimulationEventKind.Info, string.Empty, "仿真完成");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (current is not null)
                TrySafeChangeState(current, SimulationBoxState.Pending, "已停止，恢复为待堆垛");

            Raise(SimulationEventKind.Info, string.Empty, "仿真已停止");
        }
        catch (Exception ex)
        {
            if (current is not null)
                TrySafeChangeState(current, SimulationBoxState.Failed, $"堆垛失败：{ex.Message}");

            Raise(SimulationEventKind.Error, current?.BoxNumber ?? string.Empty, $"仿真异常：{ex.Message}");
        }
    }

    private void ChangeState(SimulationBoxItem box, SimulationBoxState state, string message)
    {
        _statusSink.SetState(box, state);
        BoxStateChanged?.Invoke(box, state);
        Raise(SimulationEventKind.Info, box.BoxNumber, $"箱号 {box.BoxNumber}：{message}");
    }

    private void TrySafeChangeState(SimulationBoxItem box, SimulationBoxState state, string message)
    {
        try
        {
            ChangeState(box, state, message);
        }
        catch (Exception ex)
        {
            Raise(SimulationEventKind.Error, box.BoxNumber, $"回写箱号 {box.BoxNumber} 状态失败：{ex.Message}");
        }
    }

    private void Raise(SimulationEventKind kind, string boxNumber, string message)
    {
        EventRaised?.Invoke(new SimulationEvent(DateTime.Now, kind, boxNumber, message));
    }

    private static TaskCompletionSource<bool> CreateOpenSignal()
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.TrySetResult(true);
        return signal;
    }
}
