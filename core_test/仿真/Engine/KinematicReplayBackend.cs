namespace CoreDllFunctionTester.Simulation;

/// <summary>
/// 运动学回放后端：箱子从释放高度匀速下降到目标位置，不做物理计算。
/// </summary>
public sealed class KinematicReplayBackend : ISimulationBackend
{
    private readonly SimulationOptions _options;

    /// <summary>创建运动学回放后端。</summary>
    public KinematicReplayBackend(SimulationOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public string Name => "运动学回放";

    /// <inheritdoc />
    public async Task ExecuteAsync(
        SimulationBoxItem box,
        ISimulationControl control,
        Action<SimulationPose> onPose,
        CancellationToken ct)
    {
        int steps = Math.Max(1, _options.MotionSteps);
        for (int i = 0; i <= steps; i++)
        {
            // 暂停只在步与步之间生效，避免箱子停在半空的中间状态被误解为异常。
            await control.WaitIfPausedAsync(ct);

            double progress = (double)i / steps;
            double bottomZ = box.BottomZMm + _options.ReleaseHeightMm * (1 - progress);
            onPose(new SimulationPose(box, box.XMm, box.YMm, bottomZ));

            if (i < steps)
                await Task.Delay(_options.StepDelayMs, ct);
        }
    }
}
