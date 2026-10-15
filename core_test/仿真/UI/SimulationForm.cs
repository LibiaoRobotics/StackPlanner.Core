namespace CoreDllFunctionTester.Simulation.UI;

/// <summary>
/// 堆垛仿真窗口：左侧逐箱明细，右侧等轴测视图，底部事件日志。
/// 只依赖仿真接口与模型，数据来源、状态回写和后端均由外部注入。
/// </summary>
public sealed class SimulationForm : Form
{
    /// <summary>状态列索引，与 InitializeGrid 中的列顺序保持一致。</summary>
    private const int ColState = 8;

    private readonly ISimulationDataSource _dataSource;
    private readonly ISimulationStatusSink _statusSink;
    private readonly ISimulationBackend _backend;
    private readonly SimulationOptions _options;

    private readonly Button _startButton = new() { Text = "启动仿真", AutoSize = true };
    private readonly Button _pauseButton = new() { Text = "暂停", AutoSize = true, Enabled = false };
    private readonly Button _stopButton = new() { Text = "停止仿真", AutoSize = true, Enabled = false };
    private readonly Button _refreshButton = new() { Text = "刷新数据", AutoSize = true };
    private readonly Label _statusLabel = new() { AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill };
    private readonly StackViewControl _view = new() { Dock = DockStyle.Fill };
    private readonly TextBox _logBox = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9F),
    };

    private readonly Dictionary<string, int> _rowIndexByBox = new(StringComparer.Ordinal);
    private readonly List<SimulationBoxItem> _placedBoxes = new();

    private SimulationScene? _scene;
    private SimulationRunner? _runner;
    private CancellationTokenSource? _cts;
    private bool _running;

    /// <summary>创建仿真窗口。</summary>
    public SimulationForm(
        ISimulationDataSource dataSource,
        ISimulationStatusSink statusSink,
        ISimulationBackend backend,
        SimulationOptions options)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(statusSink);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(options);

        _dataSource = dataSource;
        _statusSink = statusSink;
        _backend = backend;
        _options = options;

        Text = $"堆垛仿真（{backend.Name}）";
        Size = new Size(1280, 800);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterParent;

        InitializeLayout();
        BindEvents();
        UpdateButtons();
    }

    /// <inheritdoc />
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _split.SplitterDistance = Math.Min(430, _split.Width / 2);
        ReloadScene();
    }

    /// <inheritdoc />
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 仿真运行中禁止关闭窗口，避免后台仍在回写 Core 状态时界面已消失。
        if (_running && e.CloseReason == CloseReason.UserClosing)
        {
            MessageBox.Show(this, "仿真运行中，请先点击“停止仿真”。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Dispose();
            _cts = null;
        }

        base.Dispose(disposing);
    }

    private void InitializeLayout()
    {
        var toolPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 8, 8, 0),
        };
        toolPanel.Controls.AddRange(new Control[] { _startButton, _pauseButton, _stopButton, _refreshButton, _statusLabel });

        _split.Panel1.Controls.Add(_grid);
        _split.Panel2.Controls.Add(_view);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
        root.Controls.Add(toolPanel, 0, 0);
        root.Controls.Add(_split, 0, 1);
        root.Controls.Add(_logBox, 0, 2);
        Controls.Add(root);

        InitializeGrid();
    }

    private void InitializeGrid()
    {
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

        _grid.Columns.Add(CreateColumn("序号", "Order"));
        _grid.Columns.Add(CreateColumn("箱号", "BoxNumber"));
        _grid.Columns.Add(CreateColumn("长(mm)", "Length"));
        _grid.Columns.Add(CreateColumn("宽(mm)", "Width"));
        _grid.Columns.Add(CreateColumn("高(mm)", "Height"));
        _grid.Columns.Add(CreateColumn("重量(kg)", "Weight"));
        _grid.Columns.Add(CreateColumn("朝向", "Orientation"));
        _grid.Columns.Add(CreateColumn("层号", "Layer"));
        _grid.Columns.Add(CreateColumn("状态", "State"));
    }

    private static DataGridViewTextBoxColumn CreateColumn(string header, string name)
    {
        return new DataGridViewTextBoxColumn { HeaderText = header, Name = name, SortMode = DataGridViewColumnSortMode.NotSortable };
    }

    private void BindEvents()
    {
        _startButton.Click += (_, _) => StartSimulation();
        _pauseButton.Click += (_, _) => TogglePause();
        _stopButton.Click += (_, _) => StopSimulation();
        _refreshButton.Click += (_, _) => ReloadScene();
    }

    /// <summary>从数据源重新读取场景，刷新明细表和视图。</summary>
    private void ReloadScene()
    {
        if (_running)
            return;

        SimulationScene scene;
        try
        {
            scene = _dataSource.LoadScene();
        }
        catch (Exception ex)
        {
            _scene = null;
            _statusLabel.Text = "数据加载失败";
            Log(SimulationEventKind.Error, string.Empty, $"加载数据失败：{ex.Message}");
            return;
        }

        _scene = scene;
        _rowIndexByBox.Clear();
        _placedBoxes.Clear();
        _grid.Rows.Clear();

        foreach (var box in scene.Boxes)
        {
            var state = box.IsSucceeded ? SimulationBoxState.Succeeded : SimulationBoxState.Pending;
            if (box.IsSucceeded)
                _placedBoxes.Add(box);

            int row = _grid.Rows.Add(
                box.Order,
                box.BoxNumber,
                box.LengthMm,
                box.WidthMm,
                box.HeightMm,
                box.WeightKg,
                box.OrientationDeg,
                box.LayerIndex,
                GetStateText(state));
            _rowIndexByBox[box.BoxNumber] = row;
        }

        _view.SetScene(scene);
        _view.SetPlacedBoxes(_placedBoxes);
        _view.SetMovingPose(null);

        _statusLabel.Text = $"共 {scene.Boxes.Count} 箱，已堆 {_placedBoxes.Count} 箱，待堆 {scene.Boxes.Count - _placedBoxes.Count} 箱";
        Log(SimulationEventKind.Info, string.Empty, $"已加载 {scene.Boxes.Count} 个箱子");
    }

    private async void StartSimulation()
    {
        if (_running)
            return;

        // 每次启动前重新读取数据，保证仿真基于 Core 当前的规划结果。
        ReloadScene();
        if (_scene is null)
            return;

        var pending = _scene.Boxes.Where(b => !b.IsSucceeded).ToList();
        if (pending.Count == 0)
        {
            Log(SimulationEventKind.Info, string.Empty, "没有待堆垛的箱子");
            return;
        }

        var runner = new SimulationRunner(_backend, _statusSink, _options);
        runner.EventRaised += e => PostToUi(() => AppendLog(e));
        runner.BoxStateChanged += (box, state) => PostToUi(() => OnBoxStateChanged(box, state));
        runner.PoseChanged += pose => PostToUi(() => _view.SetMovingPose(pose));

        _runner = runner;
        _cts = new CancellationTokenSource();
        _running = true;
        UpdateButtons();

        try
        {
            await runner.RunAsync(pending, _cts.Token);
        }
        finally
        {
            _running = false;
            _runner = null;
            _cts?.Dispose();
            _cts = null;
            UpdateButtons();

            // 运行结束后以 Core 的真实状态为准重新加载一次，修正界面显示。
            ReloadScene();
        }
    }

    private void TogglePause()
    {
        if (_runner is null)
            return;

        if (_runner.IsPaused)
            _runner.Resume();
        else
            _runner.Pause();

        UpdateButtons();
    }

    private void StopSimulation()
    {
        if (_cts is null || _cts.IsCancellationRequested)
            return;

        _stopButton.Enabled = false;
        Log(SimulationEventKind.Info, string.Empty, "正在停止...");
        _cts.Cancel();
    }

    private void OnBoxStateChanged(SimulationBoxItem box, SimulationBoxState state)
    {
        if (_rowIndexByBox.TryGetValue(box.BoxNumber, out int row))
            _grid.Rows[row].Cells[ColState].Value = GetStateText(state);

        switch (state)
        {
            case SimulationBoxState.Succeeded:
                if (_placedBoxes.All(b => b.BoxNumber != box.BoxNumber))
                    _placedBoxes.Add(box);
                break;
            case SimulationBoxState.Pending:
                _placedBoxes.RemoveAll(b => b.BoxNumber == box.BoxNumber);
                break;
        }

        _view.SetPlacedBoxes(_placedBoxes);
        if (state != SimulationBoxState.Stacking)
            _view.SetMovingPose(null);
    }

    private void UpdateButtons()
    {
        _startButton.Enabled = !_running;
        _refreshButton.Enabled = !_running;
        _pauseButton.Enabled = _running;
        _stopButton.Enabled = _running;
        _pauseButton.Text = _runner?.IsPaused == true ? "继续" : "暂停";

        if (_running)
            _statusLabel.Text = _runner?.IsPaused == true ? "已暂停" : "仿真中";
    }

    /// <summary>事件可能来自非 UI 线程（后端使用 ConfigureAwait(false) 时），统一切回 UI 线程。</summary>
    private void PostToUi(Action action)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
            BeginInvoke(action);
        else
            action();
    }

    private void AppendLog(SimulationEvent e)
    {
        string prefix = e.Kind == SimulationEventKind.Error ? "[错误] " : string.Empty;
        string box = string.IsNullOrEmpty(e.BoxNumber) ? string.Empty : $"[{e.BoxNumber}] ";
        _logBox.AppendText($"{e.TimeLocal:HH:mm:ss} {prefix}{box}{e.Message}{Environment.NewLine}");
    }

    private void Log(SimulationEventKind kind, string boxNumber, string message)
    {
        AppendLog(new SimulationEvent(DateTime.Now, kind, boxNumber, message));
    }

    private static string GetStateText(SimulationBoxState state)
    {
        return state switch
        {
            SimulationBoxState.Pending => "待堆垛",
            SimulationBoxState.Stacking => "堆垛中",
            SimulationBoxState.Succeeded => "堆垛成功",
            SimulationBoxState.Failed => "堆垛失败",
            _ => state.ToString(),
        };
    }
}
