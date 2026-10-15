using System.Text.Json;
using System.Text.Json.Serialization;
using CoreDllFunctionTester.Simulation;
using CoreDllFunctionTester.Simulation.Adapters;
using CoreDllFunctionTester.Simulation.UI;
using StackPlanner.Core;

namespace CoreDllFunctionTester;

public sealed class MainForm : Form
{
    private readonly StackPlanner.Core.StackPlanner _planner = new();
    private readonly string _succeededSnapshotPath = ResolveSucceededSnapshotPath();
    private readonly string _coreLogFilePath = ResolveCoreLogFilePath();
    private readonly TextBox _jsonPathTextBox = new() { Dock = DockStyle.Fill };
    private readonly Button _browseButton = new() { Text = "选择文件", AutoSize = true };
    private readonly Button _runButton = new() { Text = "导入库存并执行利用率规划", AutoSize = true };
    private readonly TextBox _statusBoxNumberTextBox = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _statusComboBox = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _setStatusButton = new() { Text = "设置箱子状态", AutoSize = true };
    private readonly Button _removeAndAddButton = new() { Text = "减箱并补同规格", AutoSize = true };
    private readonly Button _upsertSucceededButton = new() { Text = "从本地文件恢复成功堆", AutoSize = true };
    private readonly Button _clearSucceededButton = new() { Text = "清空成功堆垛", AutoSize = true };
    private readonly Button _saveSucceededButton = new() { Text = "保存成功堆到本地文件", AutoSize = true };
    private readonly Button _continuePlanButton = new() { Text = "继续利用率规划", AutoSize = true };
    private readonly Button _exportBufferedLogsButton = new() { Text = "导出 Core 内存日志", AutoSize = true };
    private readonly Button _openSimulationButton = new() { Text = "仿真", AutoSize = true };
    private SimulationForm? _simulationForm;
    private readonly Label _statusResultLabel = new() { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _summaryLabel = new() { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly DataGridView _plannedCountsGrid = BuildReadonlyGrid();
    private readonly DataGridView _layersGrid = BuildReadonlyGrid();
    private readonly DataGridView _boxDetailsGrid = BuildReadonlyGrid();
    private readonly TextBox _rawJsonTextBox = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        ReadOnly = true,
        WordWrap = false,
    };

    public MainForm()
    {
        Text = "StackPlanner.Core DLL 功能测试工具";
        Width = 1400;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        InitializeLayout();
        BindEvents();
        InitializeStatusOptions();
        ConfigureCoreLogging();

        _jsonPathTextBox.Text = ResolveDefaultRackBoxPath();
        _summaryLabel.Text = "等待执行测试...";
        SetIdleStatusText();
    }

    private void ConfigureCoreLogging()
    {
        string? directory = Path.GetDirectoryName(_coreLogFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        _planner.ConfigureLogging(new PlannerLogOptions
        {
            Enabled = true,
            MinLevel = PlannerLogLevel.Debug,
            MaxBufferedEntries = 5000,
            FilePath = _coreLogFilePath,
        });
    }

    private void SetIdleStatusText()
    {
        _statusResultLabel.Text = $"状态接口未调用。成功堆文件: {_succeededSnapshotPath} | Core 日志: {_coreLogFilePath}";
    }

    private void InitializeStatusOptions()
    {
        _statusComboBox.Items.AddRange(Enum.GetNames<BoxStatus>());
        _statusComboBox.SelectedItem = BoxStatus.Arrived.ToString();
    }

    private static DataGridView BuildReadonlyGrid()
    {
        return new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AutoGenerateColumns = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
        };
    }

    private void InitializeLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var inputPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
        };
        inputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        inputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        inputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));

        var inputLabel = new Label
        {
            Text = "RackBox 库存文件",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
        };

        inputPanel.Controls.Add(inputLabel, 0, 0);
        inputPanel.Controls.Add(_jsonPathTextBox, 1, 0);
        inputPanel.Controls.Add(_browseButton, 2, 0);
        inputPanel.Controls.Add(_runButton, 3, 0);

        var statusPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 13,
        };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var boxNumberLabel = new Label
        {
            Text = "箱号",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
        };
        var statusLabel = new Label
        {
            Text = "目标状态",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
        };

        statusPanel.Controls.Add(boxNumberLabel, 0, 0);
        statusPanel.Controls.Add(_statusBoxNumberTextBox, 1, 0);
        statusPanel.Controls.Add(statusLabel, 2, 0);
        statusPanel.Controls.Add(_statusComboBox, 3, 0);
        statusPanel.Controls.Add(_setStatusButton, 4, 0);
        statusPanel.Controls.Add(_removeAndAddButton, 5, 0);
        statusPanel.Controls.Add(_upsertSucceededButton, 6, 0);
        statusPanel.Controls.Add(_clearSucceededButton, 7, 0);
        statusPanel.Controls.Add(_continuePlanButton, 8, 0);
        statusPanel.Controls.Add(_saveSucceededButton, 9, 0);
        statusPanel.Controls.Add(_exportBufferedLogsButton, 10, 0);
        statusPanel.Controls.Add(_openSimulationButton, 11, 0);
        statusPanel.Controls.Add(_statusResultLabel, 12, 0);

        var tabControl = new TabControl { Dock = DockStyle.Fill };
        tabControl.TabPages.Add(CreateTabPage("策略箱型数量", _plannedCountsGrid));
        tabControl.TabPages.Add(CreateTabPage("逐层利用率", _layersGrid));
        tabControl.TabPages.Add(CreateTabPage("逐箱明细", _boxDetailsGrid));
        tabControl.TabPages.Add(CreateTabPage("原始结果 JSON", _rawJsonTextBox));

        root.Controls.Add(inputPanel, 0, 0);
        root.Controls.Add(statusPanel, 0, 1);
        root.Controls.Add(_summaryLabel, 0, 2);
        root.Controls.Add(tabControl, 0, 3);

        Controls.Add(root);
    }

    private static TabPage CreateTabPage(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }

    private void BindEvents()
    {
        _browseButton.Click += (_, _) => BrowseRackBoxFile();
        _runButton.Click += (_, _) => RunTest();
        _setStatusButton.Click += (_, _) => SetBoxStatus();
        _removeAndAddButton.Click += (_, _) => RemoveAndAddSameSpecBox();
        _openSimulationButton.Click += (_, _) => OpenSimulationForm();
        _upsertSucceededButton.Click += (_, _) => UpsertSucceededPlacement();
        _clearSucceededButton.Click += (_, _) => ClearSucceededPlacements();
        _saveSucceededButton.Click += (_, _) => SaveSucceededPlacementsToFile();
        _continuePlanButton.Click += (_, _) => ContinueUtilizationPlan();
        _exportBufferedLogsButton.Click += (_, _) => ExportBufferedCoreLogs();
        _boxDetailsGrid.SelectionChanged += (_, _) => SyncSelectedBoxNumber();
    }

    private void ExportBufferedCoreLogs()
    {
        try
        {
            string exportPath = BuildBufferedLogExportPath();
            string? directory = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var logs = _planner.GetRecentLogs(maxCount: 50000);
            File.WriteAllText(exportPath, JsonSerializer.Serialize(logs, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() },
            }));

            _statusResultLabel.Text = $"导出 Core 内存日志完成：{logs.Count} 条 -> {exportPath}";
            _rawJsonTextBox.Text = JsonSerializer.Serialize(new
            {
                Operation = "ExportBufferedCoreLogs",
                BufferCount = logs.Count,
                CoreFileLogPath = _coreLogFilePath,
                ExportPath = exportPath,
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
        }
        catch (Exception ex)
        {
            _statusResultLabel.Text = $"导出 Core 内存日志失败：{ex.Message}";
            _rawJsonTextBox.Text = ex.ToString();
        }
    }

    private string BuildBufferedLogExportPath()
    {
        string baseDirectory = Path.GetDirectoryName(_coreLogFilePath)
            ?? AppContext.BaseDirectory;
        return Path.Combine(
            baseDirectory,
            $"stackplanner-core-buffered-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    }

    private void BrowseRackBoxFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 RackBox 库存 JSON",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _jsonPathTextBox.Text = dialog.FileName;
    }

    private void RunTest()
    {
        string filePath = _jsonPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            MessageBox.Show(this, "请先指定 RackBox 库存 JSON 文件路径。", "缺少输入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!File.Exists(filePath))
        {
            MessageBox.Show(this, $"文件不存在：{filePath}", "路径无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            ToggleRunningState(true);

            var records = LoadRackBoxRecords(filePath);
            var upsertResult = _planner.UpsertInventoryBoxes(records, replaceAll: true);
            var utilizationResult = _planner.PlanUtilizationFromInventory();
            var details = _planner.GetPlannerBoxDetails();

            BindPlannedCounts(utilizationResult);
            BindLayers(utilizationResult);
            BindBoxDetails(details);
            RenderSummary(upsertResult, utilizationResult, details);
            RenderRawJson(upsertResult, utilizationResult, details);
            SyncSelectedBoxNumber();
            SetIdleStatusText();
        }
        catch (Exception ex)
        {
            _summaryLabel.Text = "执行失败，请查看错误信息。";
            _rawJsonTextBox.Text = ex.ToString();
            MessageBox.Show(this, ex.Message, "执行失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            ToggleRunningState(false);
        }
    }

    private void SetBoxStatus()
    {
        string boxNumber = _statusBoxNumberTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(boxNumber))
        {
            MessageBox.Show(this, "请先输入要设置状态的箱号。", "缺少输入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_statusComboBox.SelectedItem is not string statusText
            || !Enum.TryParse<BoxStatus>(statusText, out var status))
        {
            MessageBox.Show(this, "请选择有效的目标状态。", "状态无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            bool updated = _planner.UpdateBoxStatus(boxNumber, status);
            if (!updated)
            {
                _statusResultLabel.Text = $"状态更新失败：未找到箱号 {boxNumber}。";
                return;
            }

            var details = _planner.GetPlannerBoxDetails();
            BindBoxDetails(details);
            SyncSelectedBoxNumber();
            _summaryLabel.Text = $"状态更新成功：{boxNumber} -> {status} | 已规划箱数: {details.PlannedBoxes}/{details.TotalBoxes}";
            _statusResultLabel.Text = $"状态更新成功：{boxNumber} -> {status}";
            RenderStatusRawJson(boxNumber, status, details);
        }
        catch (Exception ex)
        {
            _statusResultLabel.Text = $"状态更新失败：{ex.Message}";
            _rawJsonTextBox.Text = ex.ToString();
        }
    }

    /// <summary>
    /// 打开堆垛仿真窗口。仿真窗口只通过适配器读写 Core，同一时间只保留一个实例。
    /// </summary>
    private void OpenSimulationForm()
    {
        if (_simulationForm is { IsDisposed: false })
        {
            _simulationForm.Activate();
            return;
        }

        var adapter = new CoreSimulationAdapter(_planner);
        var options = new SimulationOptions();
        var backend = new KinematicReplayBackend(options);
        _simulationForm = new SimulationForm(adapter, adapter, backend, options) { Owner = this };
        _simulationForm.FormClosed += (_, _) =>
        {
            _simulationForm = null;
            // 仿真可能修改了箱子状态，关闭后刷新逐箱明细。
            BindBoxDetails(_planner.GetPlannerBoxDetails());
        };
        _simulationForm.Show(this);
    }

    private void RemoveAndAddSameSpecBox()
    {
        string boxNumber = _statusBoxNumberTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(boxNumber))
        {
            MessageBox.Show(this, "请先输入要减箱的箱号。", "缺少输入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var mutation = _planner.RemoveAndAddSameSpecBox(new Box
            {
                BoxNumber = boxNumber,
            });

            var details = _planner.GetPlannerBoxDetails();
            BindBoxDetails(details);
            SyncSelectedBoxNumber();

            _summaryLabel.Text =
                $"减箱补箱结果: {(mutation.Succeeded ? "成功" : "失败")} | {mutation.Message} | 已规划箱数: {details.PlannedBoxes}/{details.TotalBoxes}";
            _statusResultLabel.Text =
                $"RemoveAndAddSameSpecBox: {(mutation.Succeeded ? "成功" : "失败")} - {mutation.Message}";

            RenderMutationRawJson(boxNumber, mutation, details);
        }
        catch (Exception ex)
        {
            _statusResultLabel.Text = $"减箱补箱失败：{ex.Message}";
            _rawJsonTextBox.Text = ex.ToString();
        }
    }

    private void UpsertSucceededPlacement()
    {
        try
        {
            if (!File.Exists(_succeededSnapshotPath))
            {
                MessageBox.Show(this,
                    $"成功堆文件不存在：{_succeededSnapshotPath}\n请先点击“保存成功堆到本地文件”。",
                    "文件不存在",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var records = LoadSucceededPlacementRecords(_succeededSnapshotPath);
            if (records.Count == 0)
            {
                MessageBox.Show(this,
                    $"成功堆文件没有可恢复记录：{_succeededSnapshotPath}",
                    "无可用记录",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var result = _planner.UpsertSucceededPlacements(records, replaceAll: true);

            var details = _planner.GetPlannerBoxDetails();
            BindBoxDetails(details);
            SyncSelectedBoxNumber();

            string status = result.Invalid == 0 ? "成功" : "失败";
            _summaryLabel.Text =
                $"从本地文件恢复成功堆{status} | {result.Message} | 恢复记录: {records.Count} | 已规划箱数: {details.PlannedBoxes}/{details.TotalBoxes}";
            _statusResultLabel.Text = $"UpsertSucceededPlacements(file): {result.Message}";

            _rawJsonTextBox.Text = JsonSerializer.Serialize(new
            {
                Operation = "UpsertSucceededPlacementsFromFile",
                FilePath = _succeededSnapshotPath,
                RequestCount = records.Count,
                Result = result,
                Plan = _planner.CurrentPlan,
                Details = details,
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() },
            });
        }
        catch (Exception ex)
        {
            _statusResultLabel.Text = $"写入成功堆垛失败：{ex.Message}";
            _rawJsonTextBox.Text = ex.ToString();
        }
    }

    private void SaveSucceededPlacementsToFile()
    {
        try
        {
            var details = _planner.GetPlannerBoxDetails();
            var succeededBoxes = details.Boxes
                .Where(item => item.Status == BoxStatus.StackingSucceeded)
                .OrderBy(item => item.Order)
                .ThenBy(item => item.BoxNumber, StringComparer.Ordinal)
                .ToArray();
            if (succeededBoxes.Length == 0)
            {
                MessageBox.Show(this,
                    "当前没有 StackingSucceeded 状态的箱子可保存。",
                    "无可保存数据",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var records = new List<SucceededPlacementRecord>(succeededBoxes.Length);
            foreach (var box in succeededBoxes)
            {
                if (!box.Planned
                    || box.Xmm is null
                    || box.Ymm is null
                    || box.Zmm is null
                    || box.OrientationDeg is null
                    || box.LayerIndex is null
                    || box.Order < 0)
                {
                    MessageBox.Show(this,
                        $"箱子 {box.BoxNumber} 缺少完整落点，无法保存成功堆文件。",
                        "数据不完整",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                records.Add(new SucceededPlacementRecord
                {
                    BoxNumber = box.BoxNumber,
                    LengthMm = box.LengthMm,
                    WidthMm = box.WidthMm,
                    HeightMm = box.HeightMm,
                    WeightKg = box.WeightKg,
                    Placement = new BoxPlacement
                    {
                        Order = box.Order,
                        BoxNumber = box.BoxNumber,
                        Xmm = box.Xmm.Value,
                        Ymm = box.Ymm.Value,
                        Zmm = box.Zmm.Value,
                        OrientationDeg = box.OrientationDeg.Value,
                        LayerIndex = box.LayerIndex.Value,
                    },
                });
            }

            string? directory = Path.GetDirectoryName(_succeededSnapshotPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var payload = new SucceededPlacementSnapshot
            {
                SavedAtUtc = DateTimeOffset.UtcNow,
                Records = records,
            };

            File.WriteAllText(_succeededSnapshotPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));

            _summaryLabel.Text =
                $"保存成功堆完成 | 记录数: {records.Count} | 文件: {_succeededSnapshotPath}";
            _statusResultLabel.Text = $"保存成功堆完成：{records.Count} 条";
            _rawJsonTextBox.Text = JsonSerializer.Serialize(new
            {
                Operation = "SaveSucceededPlacementsToFile",
                FilePath = _succeededSnapshotPath,
                Snapshot = payload,
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
        }
        catch (Exception ex)
        {
            _statusResultLabel.Text = $"保存成功堆失败：{ex.Message}";
            _rawJsonTextBox.Text = ex.ToString();
        }
    }

    private void ClearSucceededPlacements()
    {
        try
        {
            int cleared = _planner.ClearSucceededPlacements();
            var details = _planner.GetPlannerBoxDetails();

            BindBoxDetails(details);
            SyncSelectedBoxNumber();

            _summaryLabel.Text =
                $"清空成功堆垛完成 | 恢复箱数: {cleared} | 已规划箱数: {details.PlannedBoxes}/{details.TotalBoxes}";
            _statusResultLabel.Text = $"ClearSucceededPlacements: 已恢复 {cleared} 个箱子";

            _rawJsonTextBox.Text = JsonSerializer.Serialize(new
            {
                Operation = "ClearSucceededPlacements",
                Cleared = cleared,
                Plan = _planner.CurrentPlan,
                Details = details,
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() },
            });
        }
        catch (Exception ex)
        {
            _statusResultLabel.Text = $"清空成功堆垛失败：{ex.Message}";
            _rawJsonTextBox.Text = ex.ToString();
        }
    }

    private void ContinueUtilizationPlan()
    {
        try
        {
            var utilization = _planner.PlanUtilizationFromInventory();
            var details = _planner.GetPlannerBoxDetails();

            BindPlannedCounts(utilization);
            BindLayers(utilization);
            BindBoxDetails(details);
            SyncSelectedBoxNumber();

            int succeededCount = details.Boxes.Count(x => x.Status == BoxStatus.StackingSucceeded);
            _summaryLabel.Text =
                $"继续利用率规划: {(utilization.PlanningResult ? "成功" : "失败")} | {utilization.Message} | 成功堆垛: {succeededCount} | 已规划箱数: {details.PlannedBoxes}/{details.TotalBoxes}";
            _statusResultLabel.Text = $"PlanUtilizationFromInventory: {utilization.Message}";

            _rawJsonTextBox.Text = JsonSerializer.Serialize(new
            {
                Operation = "PlanUtilizationFromInventory",
                Result = utilization,
                Plan = _planner.CurrentPlan,
                Details = details,
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() },
            });
        }
        catch (Exception ex)
        {
            _statusResultLabel.Text = $"继续利用率规划失败：{ex.Message}";
            _rawJsonTextBox.Text = ex.ToString();
        }
    }

    private void SyncSelectedBoxNumber()
    {
        if (_boxDetailsGrid.CurrentRow?.DataBoundItem is null)
            return;

        string? selected = _boxDetailsGrid.CurrentRow.Cells[nameof(PlannerBoxDetail.BoxNumber)]?.Value?.ToString();
        if (!string.IsNullOrWhiteSpace(selected))
            _statusBoxNumberTextBox.Text = selected;
    }

    private static double? TryReadDoubleCell(DataGridViewRow row, string cellName)
    {
        var value = row.Cells[cellName]?.Value;
        if (value is null)
            return null;

        if (value is double d)
            return d;
        if (value is float f)
            return f;
        if (value is decimal m)
            return (double)m;

        return double.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static int? TryReadIntCell(DataGridViewRow row, string cellName)
    {
        var value = row.Cells[cellName]?.Value;
        if (value is null)
            return null;

        if (value is int i)
            return i;
        if (value is long l && l >= int.MinValue && l <= int.MaxValue)
            return (int)l;

        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static List<SucceededPlacementRecord> LoadSucceededPlacementRecords(string filePath)
    {
        string json = File.ReadAllText(filePath);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        var snapshot = JsonSerializer.Deserialize<SucceededPlacementSnapshot>(json, options);
        if (snapshot?.Records is { Count: > 0 })
            return snapshot.Records;

        return JsonSerializer.Deserialize<List<SucceededPlacementRecord>>(json, options)
               ?? new List<SucceededPlacementRecord>();
    }

    private static List<InventoryBoxRecord> LoadRackBoxRecords(string filePath)
    {
        string json = File.ReadAllText(filePath);
        var source = JsonSerializer.Deserialize<Dictionary<string, RackBoxEntry>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? new Dictionary<string, RackBoxEntry>(StringComparer.Ordinal);

        var records = new List<InventoryBoxRecord>(source.Count);
        foreach (var pair in source)
        {
            if (pair.Value is null)
                continue;

            records.Add(new InventoryBoxRecord
            {
                BoxNumber = pair.Key,
                LengthMm = pair.Value.Length * 1000,
                HeightMm = pair.Value.Height * 1000,
                DepthMm = pair.Value.Depth * 1000,
                WeightKg = pair.Value.Weight,
                Source = "RackBox",
            });
        }

        return records;
    }

    private void BindPlannedCounts(InventoryUtilizationPlanResult result)
    {
        var rows = result.PlannedCounts.Select(item => new
        {
            item.LengthMm,
            item.WidthMm,
            item.HeightMm,
            item.Count,
        }).ToList();

        _plannedCountsGrid.DataSource = rows;
    }

    private void BindLayers(InventoryUtilizationPlanResult result)
    {
        var rows = result.Layers.Select(item => new
        {
            item.LayerIndex,
            UtilizationPercent = $"{item.Utilization:P2}",
        }).ToList();

        _layersGrid.DataSource = rows;
    }

    private void BindBoxDetails(PlannerBoxDetailResult result)
    {
        var rows = result.Boxes.Select(item => new
        {
            item.Order,
            item.BoxNumber,
            item.TypeId,
            item.LengthMm,
            item.WidthMm,
            item.HeightMm,
            item.WeightKg,
            Status = item.Status.ToString(),
            item.Planned,
            item.Xmm,
            item.Ymm,
            item.Zmm,
            item.OrientationDeg,
            item.LayerIndex,
        }).ToList();

        _boxDetailsGrid.DataSource = rows;
    }

    private void RenderSummary(
        InventoryUpsertResult upsert,
        InventoryUtilizationPlanResult utilization,
        PlannerBoxDetailResult details)
    {
        _summaryLabel.Text =
            $"入库总数: {upsert.Total} | 无效: {upsert.Invalid} | 规划成功: {utilization.PlanningResult} | 已应用: {utilization.Applied} | 选中箱数: {utilization.SelectedBoxCount} | 利用率: {utilization.Utilization:P2} | 已规划箱数: {details.PlannedBoxes}/{details.TotalBoxes}";
    }

    private void RenderRawJson(
        InventoryUpsertResult upsert,
        InventoryUtilizationPlanResult utilization,
        PlannerBoxDetailResult details)
    {
        var payload = new
        {
            Upsert = upsert,
            Utilization = utilization,
            Plan = _planner.CurrentPlan,
            Details = details,
        };

        _rawJsonTextBox.Text = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        });
    }

    private void RenderStatusRawJson(string boxNumber, BoxStatus status, PlannerBoxDetailResult details)
    {
        var payload = new
        {
            Operation = "UpdateBoxStatus",
            BoxNumber = boxNumber,
            Status = status,
            Plan = _planner.CurrentPlan,
            Details = details,
        };

        _rawJsonTextBox.Text = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        });
    }

    private void RenderMutationRawJson(
        string boxNumber,
        UtilizationMutationResult mutation,
        PlannerBoxDetailResult details)
    {
        var payload = new
        {
            Operation = "RemoveAndAddSameSpecBox",
            BoxNumber = boxNumber,
            Mutation = mutation,
            Plan = _planner.CurrentPlan,
            Details = details,
        };

        _rawJsonTextBox.Text = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        });
    }

    private void ToggleRunningState(bool isRunning)
    {
        UseWaitCursor = isRunning;
        _runButton.Enabled = !isRunning;
        _browseButton.Enabled = !isRunning;
        _setStatusButton.Enabled = !isRunning;
        _removeAndAddButton.Enabled = !isRunning;
        _upsertSucceededButton.Enabled = !isRunning;
        _clearSucceededButton.Enabled = !isRunning;
        _saveSucceededButton.Enabled = !isRunning;
        _continuePlanButton.Enabled = !isRunning;
        _exportBufferedLogsButton.Enabled = !isRunning;
    }

    private static string ResolveDefaultRackBoxPath()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "普通飞箱和吸盘飞箱共存V5_RackBox.json")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "普通飞箱和吸盘飞箱共存V5_RackBox.json")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "core_test", "普通飞箱和吸盘飞箱共存V5_RackBox.json")),
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string ResolveSucceededSnapshotPath()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "core_test", "succeeded_placements.local.json")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "succeeded_placements.local.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "succeeded_placements.local.json")),
        };

        foreach (string path in candidates)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                return path;
        }

        return candidates[^1];
    }

    private static string ResolveCoreLogFilePath()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "core_test", "core_logs", "stackplanner-core.log")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "core_logs", "stackplanner-core.log")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "core_logs", "stackplanner-core.log")),
        };

        foreach (string path in candidates)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                return path;
        }

        return candidates[^1];
    }

    private sealed class SucceededPlacementSnapshot
    {
        public DateTimeOffset SavedAtUtc { get; set; }
        public List<SucceededPlacementRecord> Records { get; set; } = new();
    }

    private sealed class RackBoxEntry
    {
        public double Length { get; set; }
        public double Height { get; set; }
        public double Depth { get; set; }
        public double Weight { get; set; }
    }
}
