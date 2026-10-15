using System.Drawing.Drawing2D;

namespace CoreDllFunctionTester.Simulation.UI;

/// <summary>
/// 等轴测可视化控件：绘制托盘、已堆箱子和运动中的箱子。只负责绘制，不包含仿真逻辑。
/// 投影口径：屏幕 X = (x - y) * cos30，屏幕 Y = (x + y) / 2 - z。
/// </summary>
public sealed class StackViewControl : Control
{
    private const double Cos30 = 0.86602540378;

    private static readonly Color[] Palette =
    {
        Color.FromArgb(242, 150, 110),
        Color.FromArgb(120, 160, 235),
        Color.FromArgb(110, 200, 150),
        Color.FromArgb(165, 140, 225),
        Color.FromArgb(235, 205, 100),
        Color.FromArgb(100, 190, 210),
    };

    private readonly Dictionary<string, Color> _colorByDimension = new(StringComparer.Ordinal);
    private SimulationScene? _scene;
    private List<SimulationBoxItem> _placed = new();
    private SimulationPose? _moving;
    private int _totalBoxes;
    private double _centerX;
    private double _centerY;
    private double _scale = 1;
    private PointF _origin;

    /// <summary>创建等轴测可视化控件。</summary>
    public StackViewControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        BackColor = Color.FromArgb(238, 242, 248);
    }

    /// <summary>设置仿真场景（托盘范围与总箱数），并清空已堆箱子。</summary>
    public void SetScene(SimulationScene scene)
    {
        _scene = scene;
        _totalBoxes = scene.Boxes.Count;
        _placed = new List<SimulationBoxItem>();
        _moving = null;
        RecalculateLayout();
        Invalidate();
    }

    /// <summary>设置已堆垛成功的箱子。</summary>
    public void SetPlacedBoxes(IEnumerable<SimulationBoxItem> boxes)
    {
        _placed = boxes.ToList();
        Invalidate();
    }

    /// <summary>设置运动中的箱子位姿，传入 null 表示清除。</summary>
    public void SetMovingPose(SimulationPose? pose)
    {
        _moving = pose;
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RecalculateLayout();
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_scene is null)
        {
            using var hintBrush = new SolidBrush(Color.Gray);
            g.DrawString("暂无规划数据", Font, hintBrush, 10, 10);
            return;
        }

        DrawPallet(g, _scene);

        var renderList = new List<RenderBox>(_placed.Count + 1);
        foreach (var box in _placed)
            renderList.Add(new RenderBox(box, box.XMm, box.YMm, box.BottomZMm, false));
        if (_moving is not null)
            renderList.Add(new RenderBox(_moving.Box, _moving.XMm, _moving.YMm, _moving.BottomZMm, true));

        // 画家算法：远处（x+y+z 小）先画，近处后画，保证堆叠遮挡关系正确。
        foreach (var item in renderList.OrderBy(Depth))
            DrawBox(g, item);

        using var textBrush = new SolidBrush(Color.DimGray);
        g.DrawString($"已堆 {_placed.Count} / {_totalBoxes}    视角：等轴测（仅可视化，无物理）", Font, textBrush, 10, 8);
    }

    private void RecalculateLayout()
    {
        if (_scene is null)
            return;

        double width = _scene.PalletXMaxMm - _scene.PalletXMinMm;
        double height = _scene.PalletYMaxMm - _scene.PalletYMinMm;
        double zMax = Math.Max(_scene.StackMaxHeightMm, 1);
        double availableWidth = Math.Max(1, ClientSize.Width - 40);
        double availableHeight = Math.Max(1, ClientSize.Height - 40);

        // 水平方向跨度 = (W + H) * cos30；垂直方向跨度 = (W + H) / 2 + 最大堆高。
        double scaleByWidth = availableWidth / ((width + height) * Cos30);
        double scaleByHeight = availableHeight / ((width + height) / 2 + zMax);
        _scale = Math.Min(scaleByWidth, scaleByHeight);

        _centerX = (_scene.PalletXMinMm + _scene.PalletXMaxMm) / 2;
        _centerY = (_scene.PalletYMinMm + _scene.PalletYMaxMm) / 2;
        // 让垂直方向的包围盒中心（投影高度 -zMax/2）落在控件中心。
        _origin = new PointF(
            (float)(ClientSize.Width / 2.0),
            (float)(ClientSize.Height / 2.0 + zMax / 2 * _scale));
    }

    private PointF Project(double x, double y, double z)
    {
        double dx = x - _centerX;
        double dy = y - _centerY;
        double screenX = _origin.X + (dx - dy) * Cos30 * _scale;
        double screenY = _origin.Y + ((dx + dy) * 0.5 - z) * _scale;
        return new PointF((float)screenX, (float)screenY);
    }

    private void DrawPallet(Graphics g, SimulationScene scene)
    {
        var corners = new[]
        {
            Project(scene.PalletXMinMm, scene.PalletYMinMm, 0),
            Project(scene.PalletXMaxMm, scene.PalletYMinMm, 0),
            Project(scene.PalletXMaxMm, scene.PalletYMaxMm, 0),
            Project(scene.PalletXMinMm, scene.PalletYMaxMm, 0),
        };

        using var fillBrush = new SolidBrush(Color.FromArgb(200, 210, 222));
        using var edgePen = new Pen(Color.FromArgb(90, 105, 125), 1.5f);
        g.FillPolygon(fillBrush, corners);
        g.DrawPolygon(edgePen, corners);
    }

    private void DrawBox(Graphics g, RenderBox item)
    {
        var box = item.Box;
        double halfX = box.FootprintXMm / 2;
        double halfY = box.FootprintYMm / 2;
        double x0 = item.X - halfX;
        double x1 = item.X + halfX;
        double y0 = item.Y - halfY;
        double y1 = item.Y + halfY;
        double z0 = item.BottomZ;
        double z1 = item.BottomZ + box.HeightMm;

        // 只绘制朝向观察者的三个面：顶面、+X 面、+Y 面。
        var topFace = new[] { Project(x0, y0, z1), Project(x1, y0, z1), Project(x1, y1, z1), Project(x0, y1, z1) };
        var sideXFace = new[] { Project(x1, y0, z0), Project(x1, y1, z0), Project(x1, y1, z1), Project(x1, y0, z1) };
        var sideYFace = new[] { Project(x0, y1, z0), Project(x1, y1, z0), Project(x1, y1, z1), Project(x0, y1, z1) };

        Color baseColor = GetColor(box);
        using var topBrush = new SolidBrush(ControlPaint.Light(baseColor));
        using var sideXBrush = new SolidBrush(baseColor);
        using var sideYBrush = new SolidBrush(ControlPaint.Dark(baseColor));
        using var edgePen = new Pen(
            item.Moving ? Color.OrangeRed : Color.FromArgb(70, 70, 70),
            item.Moving ? 2.5f : 1f);

        g.FillPolygon(topBrush, topFace);
        g.FillPolygon(sideXBrush, sideXFace);
        g.FillPolygon(sideYBrush, sideYFace);
        g.DrawPolygon(edgePen, topFace);
        g.DrawPolygon(edgePen, sideXFace);
        g.DrawPolygon(edgePen, sideYFace);
    }

    private double Depth(RenderBox item)
    {
        return (item.X - _centerX) + (item.Y - _centerY) + item.BottomZ + item.Box.HeightMm / 2;
    }

    private Color GetColor(SimulationBoxItem box)
    {
        // 同规格箱子使用同一颜色，便于区分不同规格。
        string key = $"{box.LengthMm:0}x{box.WidthMm:0}x{box.HeightMm:0}";
        if (!_colorByDimension.TryGetValue(key, out var color))
        {
            color = Palette[_colorByDimension.Count % Palette.Length];
            _colorByDimension[key] = color;
        }

        return color;
    }

    private sealed record RenderBox(SimulationBoxItem Box, double X, double Y, double BottomZ, bool Moving);
}
