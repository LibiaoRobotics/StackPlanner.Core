using System.Text.Json;

namespace StackPlanner.Core;

/// <summary>
/// 按本地 stackplanner.config 的箱型定义对库存箱子分类。
/// 分类容差固定为 ±30mm，不匹配配置定义归为“未识别”，尺寸存在 0 或非正值归为“非法”。
/// </summary>
internal static class BoxTypeClassifier
{
    private const double RecognitionToleranceMm = 30;
    private const string ConfigFileName = "stackplanner.config";
    private const string WorkspaceCoreDirectoryName = "StackPlanner.Core";
    private const string UnrecognizedTypeId = "BT-UNRECOGNIZED";
    private const string InvalidTypeId = "BT-INVALID";

    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static BoxTypeClassificationResult Classify(
        IReadOnlyList<InventoryBoxRecord> boxes,
        double toleranceMm)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        if (!double.IsFinite(toleranceMm))
            throw new ArgumentOutOfRangeException(nameof(toleranceMm), "容差参数必须是有限数值。");

        var configuredTypes = LoadConfiguredTypes();
        var accumulators = configuredTypes
            .ToDictionary(
                type => type.TypeId,
                type => new ClusterAccumulator(type.TypeId, type.RepLengthMm, type.RepHeightMm, type.RepDepthMm),
                StringComparer.Ordinal);
        var unrecognized = new ClusterAccumulator(UnrecognizedTypeId, 0, 0, 0);
        var invalid = new ClusterAccumulator(InvalidTypeId, 0, 0, 0);
        var assignments = new List<BoxTypeAssignment>(boxes.Count);

        foreach (var box in boxes)
        {
            if (IsInvalidBox(box))
            {
                invalid.Add(box);
                assignments.Add(CreateAssignment(box, InvalidTypeId));
                continue;
            }

            var matched = FindBestMatchedType(box, configuredTypes);
            if (matched is null)
            {
                unrecognized.Add(box);
                assignments.Add(CreateAssignment(box, UnrecognizedTypeId));
                continue;
            }

            accumulators[matched.TypeId].Add(box);
            assignments.Add(CreateAssignment(
                box,
                matched.TypeId,
                (matched.RepLengthMm, matched.RepHeightMm, matched.RepDepthMm)));
        }

        var clusters = new List<BoxTypeClusterSummary>();
        foreach (var configured in configuredTypes)
        {
            var accumulator = accumulators[configured.TypeId];
            if (accumulator.Count > 0)
                clusters.Add(accumulator.ToSummary());
        }

        if (unrecognized.Count > 0)
            clusters.Add(unrecognized.ToSummary());
        if (invalid.Count > 0)
            clusters.Add(invalid.ToSummary());

        return new BoxTypeClassificationResult
        {
            ToleranceMm = RecognitionToleranceMm,
            TotalBoxes = boxes.Count,
            ClusterCount = clusters.Count,
            Clusters = clusters,
            Assignments = assignments
                .OrderBy(item => item.BoxNumber, StringComparer.Ordinal)
                .ToArray(),
            GeneratedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private static BoxTypeAssignment CreateAssignment(
        InventoryBoxRecord box,
        string typeId,
        (double LengthMm, double HeightMm, double DepthMm)? baseSize = null)
        => new()
        {
            BoxNumber = box.BoxNumber,
            TypeId = typeId,
            LengthMm = box.LengthMm,
            HeightMm = box.HeightMm,
            DepthMm = box.DepthMm,
            WeightKg = box.WeightKg,
            BaseLengthMm = baseSize?.LengthMm,
            BaseHeightMm = baseSize?.HeightMm,
            BaseDepthMm = baseSize?.DepthMm,
            Source = box.Source,
        };

    private static bool IsInvalidBox(InventoryBoxRecord box)
        => !double.IsFinite(box.LengthMm)
        || !double.IsFinite(box.HeightMm)
        || !double.IsFinite(box.DepthMm)
        || !double.IsFinite(box.WeightKg)
        || box.LengthMm <= 0
        || box.HeightMm <= 0
        || box.DepthMm <= 0;

    private static ConfiguredType? FindBestMatchedType(
        InventoryBoxRecord box,
        IReadOnlyList<ConfiguredType> configuredTypes)
    {
        // 不做归一化：箱子 长=DepthMm、高=HeightMm、宽=LengthMm，与配置 长=RepLengthMm、高=RepHeightMm、宽=RepDepthMm 逐轴比较
        var (boxDim1, boxDim2, boxDim3) = (box.DepthMm, box.HeightMm, box.LengthMm);

        ConfiguredType? best = null;
        double bestDistance = double.MaxValue;

        foreach (var configuredType in configuredTypes)
        {
            var (typeDim1, typeDim2, typeDim3) = (configuredType.RepLengthMm, configuredType.RepHeightMm, configuredType.RepDepthMm);
            if (!WithinTolerance(boxDim1, typeDim1)
                || !WithinTolerance(boxDim2, typeDim2)
                || !WithinTolerance(boxDim3, typeDim3))
            {
                continue;
            }

            double distance = Distance(boxDim1, boxDim2, boxDim3, typeDim1, typeDim2, typeDim3);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = configuredType;
                continue;
            }

            if (Math.Abs(distance - bestDistance) <= 0.0001
                && best is not null
                && string.CompareOrdinal(configuredType.TypeId, best.TypeId) < 0)
            {
                best = configuredType;
            }
        }

        return best;
    }

    private static bool WithinTolerance(double value, double reference)
        => Math.Abs(value - reference) <= RecognitionToleranceMm;

    /// <summary>返回 stackplanner.config 中全部箱型定义，供外部（如界面）读取。</summary>
    public static IReadOnlyList<BoxTypeDefinition> GetDefinitions()
        => LoadConfiguredTypes()
            .Select(type => new BoxTypeDefinition
            {
                TypeId = type.TypeId,
                RepLengthMm = type.RepLengthMm,
                RepHeightMm = type.RepHeightMm,
                RepDepthMm = type.RepDepthMm,
            })
            .ToArray();

    private static IReadOnlyList<ConfiguredType> LoadConfiguredTypes()
    {
        string configPath = ResolveConfigPath();
        using var stream = File.OpenRead(configPath);
        var config = JsonSerializer.Deserialize<BoxTypeConfigFile>(stream, ConfigJsonOptions)
            ?? throw new InvalidDataException($"无法解析箱型配置文件：{configPath}");
        if (config.Types is null || config.Types.Count == 0)
            throw new InvalidDataException($"箱型配置文件未包含有效的 types：{configPath}");

        var result = new List<ConfiguredType>(config.Types.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawType in config.Types)
        {
            string typeId = rawType.TypeId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(typeId))
                throw new InvalidDataException($"箱型配置存在空 typeId：{configPath}");
            if (!seen.Add(typeId))
                throw new InvalidDataException($"箱型配置存在重复 typeId：{typeId}");
            if (!IsPositiveFinite(rawType.RepLengthMm)
                || !IsPositiveFinite(rawType.RepHeightMm)
                || !IsPositiveFinite(rawType.RepDepthMm))
            {
                throw new InvalidDataException($"箱型配置尺寸必须大于 0 且为有限值：{typeId}");
            }

            result.Add(new ConfiguredType(typeId, rawType.RepLengthMm, rawType.RepHeightMm, rawType.RepDepthMm));
        }

        return result;
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;

    private static string ResolveConfigPath()
    {
        foreach (var startPath in EnumerateSearchRoots())
        {
            foreach (var directory in EnumerateDirectoryAndAncestors(startPath))
            {
                string directPath = Path.Combine(directory, ConfigFileName);
                if (File.Exists(directPath))
                    return directPath;

                string workspacePath = Path.Combine(directory, WorkspaceCoreDirectoryName, ConfigFileName);
                if (File.Exists(workspacePath))
                    return workspacePath;
            }
        }

        throw new FileNotFoundException($"未找到箱型配置文件 {ConfigFileName}。", ConfigFileName);
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        yield return Directory.GetCurrentDirectory();
        yield return AppContext.BaseDirectory;

        string? assemblyDirectory = Path.GetDirectoryName(typeof(BoxTypeClassifier).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            yield return assemblyDirectory;
    }

    private static IEnumerable<string> EnumerateDirectoryAndAncestors(string startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
            yield break;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new DirectoryInfo(Path.GetFullPath(startPath));
        while (current is not null)
        {
            if (visited.Add(current.FullName))
                yield return current.FullName;
            current = current.Parent;
        }
    }

    private static double Distance(
        double left1,
        double left2,
        double left3,
        double right1,
        double right2,
        double right3)
        => Math.Abs(left1 - right1) + Math.Abs(left2 - right2) + Math.Abs(left3 - right3);

    private sealed class ClusterAccumulator
    {
        private readonly string _typeId;
        private readonly double _repLengthMm;
        private readonly double _repHeightMm;
        private readonly double _repDepthMm;
        private double _weightSum;
        private double _weightMin;
        private double _weightMax;

        public ClusterAccumulator(string typeId, double repLengthMm, double repHeightMm, double repDepthMm)
        {
            _typeId = typeId;
            _repLengthMm = repLengthMm;
            _repHeightMm = repHeightMm;
            _repDepthMm = repDepthMm;
        }

        public int Count { get; private set; }

        public void Add(InventoryBoxRecord item)
        {
            if (Count == 0)
            {
                _weightMin = item.WeightKg;
                _weightMax = item.WeightKg;
            }
            else
            {
                _weightMin = Math.Min(_weightMin, item.WeightKg);
                _weightMax = Math.Max(_weightMax, item.WeightKg);
            }

            Count++;
            _weightSum += item.WeightKg;
        }

        public BoxTypeClusterSummary ToSummary()
        {
            if (Count <= 0)
                throw new InvalidOperationException("分类簇为空，不能生成摘要。");

            return new BoxTypeClusterSummary
            {
                TypeId = _typeId,
                Count = Count,
                RepLengthMm = Math.Round(_repLengthMm, 3),
                RepHeightMm = Math.Round(_repHeightMm, 3),
                RepDepthMm = Math.Round(_repDepthMm, 3),
                WeightAvgKg = Math.Round(_weightSum / Count, 3),
                WeightMinKg = Math.Round(_weightMin, 3),
                WeightMaxKg = Math.Round(_weightMax, 3),
            };
        }
    }

    private sealed record ConfiguredType(
        string TypeId,
        double RepLengthMm,
        double RepHeightMm,
        double RepDepthMm);

    private sealed class BoxTypeConfigFile
    {
        public IReadOnlyList<BoxTypeConfigEntry> Types { get; init; } = Array.Empty<BoxTypeConfigEntry>();
    }

    private sealed class BoxTypeConfigEntry
    {
        public string TypeId { get; init; } = string.Empty;
        public double RepLengthMm { get; init; }
        public double RepHeightMm { get; init; }
        public double RepDepthMm { get; init; }
    }
}
