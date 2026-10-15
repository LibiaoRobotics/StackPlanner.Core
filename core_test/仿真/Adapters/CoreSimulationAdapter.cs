using StackPlanner.Core;

namespace CoreDllFunctionTester.Simulation.Adapters;

/// <summary>
/// 仿真模块与 StackPlanner.Core 之间的唯一适配点。
/// 仿真引擎与界面不引用 Core，只通过本适配器读取规划结果并回写箱子状态。
/// 替换或删除 Core 对接时，只需修改本文件。
/// </summary>
public sealed class CoreSimulationAdapter : ISimulationDataSource, ISimulationStatusSink
{
    private readonly StackPlanner.Core.StackPlanner _planner;

    /// <summary>创建适配器，使用调用方持有的 Core 规划器实例（同一份内存状态）。</summary>
    public CoreSimulationAdapter(StackPlanner.Core.StackPlanner planner)
    {
        _planner = planner;
    }

    /// <inheritdoc />
    public SimulationScene LoadScene()
    {
        var details = _planner.GetPlannerBoxDetails();
        var boxes = details.Boxes
            .Where(x => x.Planned
                        && x.Xmm.HasValue
                        && x.Ymm.HasValue
                        && x.Zmm.HasValue
                        && x.OrientationDeg.HasValue
                        && x.LayerIndex.HasValue)
            .OrderBy(x => x.Order)
            .ThenBy(x => x.BoxNumber, StringComparer.Ordinal)
            .Select(x => new SimulationBoxItem
            {
                BoxNumber = x.BoxNumber,
                Order = x.Order,
                LengthMm = x.LengthMm,
                WidthMm = x.WidthMm,
                HeightMm = x.HeightMm,
                WeightKg = x.WeightKg,
                XMm = x.Xmm!.Value,
                YMm = x.Ymm!.Value,
                // Core 的 Zmm = PlaceFloorZMm - 箱底高度 - 箱高，这里反算出箱底高度。
                BottomZMm = StackPlanner.Core.StackPlanner.PlaceFloorZMm - x.Zmm!.Value - x.HeightMm,
                OrientationDeg = x.OrientationDeg!.Value,
                LayerIndex = x.LayerIndex!.Value,
                IsSucceeded = x.Status == BoxStatus.StackingSucceeded,
            })
            .ToList();

        return new SimulationScene
        {
            PalletXMinMm = StackPlanner.Core.StackPlanner.PalletXMinMm,
            PalletXMaxMm = StackPlanner.Core.StackPlanner.PalletXMaxMm,
            PalletYMinMm = StackPlanner.Core.StackPlanner.PalletYMinMm,
            PalletYMaxMm = StackPlanner.Core.StackPlanner.PalletYMaxMm,
            StackMaxHeightMm = StackPlanner.Core.StackPlanner.StackMaxHeightMm,
            Boxes = boxes,
        };
    }

    /// <inheritdoc />
    public void SetState(SimulationBoxItem box, SimulationBoxState state)
    {
        BoxStatus coreStatus;
        BoxPlacement? placement = null;

        switch (state)
        {
            case SimulationBoxState.Pending:
                coreStatus = BoxStatus.OnShelf;
                break;
            case SimulationBoxState.Stacking:
                coreStatus = BoxStatus.Stacking;
                break;
            case SimulationBoxState.Succeeded:
                coreStatus = BoxStatus.StackingSucceeded;
                placement = ToPlacement(box);
                break;
            case SimulationBoxState.Failed:
                coreStatus = BoxStatus.StackingFailed;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "不支持的仿真状态。");
        }

        if (!_planner.UpdateBoxStatus(box.BoxNumber, coreStatus, placement))
            throw new InvalidOperationException($"Core 中不存在箱号 {box.BoxNumber}。");
    }

    private static BoxPlacement ToPlacement(SimulationBoxItem box)
    {
        return new BoxPlacement
        {
            Order = box.Order,
            BoxNumber = box.BoxNumber,
            Xmm = box.XMm,
            Ymm = box.YMm,
            Zmm = StackPlanner.Core.StackPlanner.PlaceFloorZMm - box.BottomZMm - box.HeightMm,
            OrientationDeg = box.OrientationDeg,
            LayerIndex = box.LayerIndex,
        };
    }
}
