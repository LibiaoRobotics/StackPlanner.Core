namespace StackPlanner.Core;

/// <summary>
/// 按 length/height/depth 三维容差对库存箱子进行分类。
/// 分类与输入顺序无关，保证相同输入下结果稳定。
/// </summary>
internal static class BoxTypeClassifier
{
    private const int MaxIterations = 20;
    private const double SnapStepMm = 10.0;

    public static BoxTypeClassificationResult Classify(
        IReadOnlyList<InventoryBoxRecord> boxes,
        double toleranceMm,
        bool normalizeDimensions)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        if (!double.IsFinite(toleranceMm) || toleranceMm < 0)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm), "容差必须是大于等于 0 的有限数值。");

        // 先归一化并做稳定排序，确保同一批输入重复执行结果一致。
        var ordered = boxes
            .Select(box =>
            {
                var dims = normalizeDimensions
                    ? NormalizeDimensions(box.LengthMm, box.HeightMm, box.DepthMm)
                    : new[] { box.LengthMm, box.HeightMm, box.DepthMm };
                return new ClassifiedItem(box, dims[0], dims[1], dims[2]);
            })
            .OrderBy(x => x.Dim1)
            .ThenBy(x => x.Dim2)
            .ThenBy(x => x.Dim3)
            .ThenBy(x => x.Item.BoxNumber, StringComparer.Ordinal)
            .ToArray();

        if (ordered.Length == 0)
        {
            return new BoxTypeClassificationResult
            {
                ToleranceMm = toleranceMm,
                TotalBoxes = 0,
                ClusterCount = 0,
                Clusters = Array.Empty<BoxTypeClusterSummary>(),
                GeneratedAtUtc = DateTimeOffset.UtcNow,
            };
        }

        // 先做一次贪心初始化，再通过迭代“重分配 + 合并”收敛。
        var groupedClusters = BuildGreedyClusters(ordered, toleranceMm);
        var previousSignature = BuildClusterSignature(groupedClusters);
        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            groupedClusters = ReassignItems(ordered, groupedClusters, toleranceMm);
            MergeClusters(groupedClusters, toleranceMm);

            string signature = BuildClusterSignature(groupedClusters);
            if (string.Equals(signature, previousSignature, StringComparison.Ordinal))
                break;
            previousSignature = signature;
        }

        // 将类别代表尺寸吸附到 10mm 网格，并按吸附后尺寸再合并，进一步压缩类别数。
        var snappedClusters = MergeBySnappedDimensions(groupedClusters);

        var rawClusters = snappedClusters
            .Select(ToCluster)
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.RepLengthMm)
            .ThenBy(x => x.RepHeightMm)
            .ThenBy(x => x.RepDepthMm)
            .ToArray();

        var finalClusters = rawClusters
            .Select((x, index) => new BoxTypeClusterSummary
            {
                TypeId = $"BT-{index + 1:0000}",
                Count = x.Count,
                RepLengthMm = x.RepLengthMm,
                RepHeightMm = x.RepHeightMm,
                RepDepthMm = x.RepDepthMm,
                WeightAvgKg = x.WeightAvgKg,
                WeightMinKg = x.WeightMinKg,
                WeightMaxKg = x.WeightMaxKg,
            })
            .ToArray();

        return new BoxTypeClassificationResult
        {
            ToleranceMm = toleranceMm,
            TotalBoxes = ordered.Length,
            ClusterCount = finalClusters.Length,
            Clusters = finalClusters,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private sealed record ClassifiedItem(
        InventoryBoxRecord Item,
        double Dim1,
        double Dim2,
        double Dim3);

    private static List<WorkingCluster> BuildGreedyClusters(
        IReadOnlyList<ClassifiedItem> items,
        double toleranceMm)
    {
        var clusters = new List<WorkingCluster>();
        foreach (var item in items)
        {
            var target = clusters.FirstOrDefault(cluster =>
                IsSameType(item.Dim1, item.Dim2, item.Dim3, cluster.Rep1, cluster.Rep2, cluster.Rep3, toleranceMm));
            if (target is null)
            {
                clusters.Add(new WorkingCluster(item));
                continue;
            }
            target.Add(item);
        }
        return clusters;
    }

    private static List<WorkingCluster> ReassignItems(
        IReadOnlyList<ClassifiedItem> items,
        IReadOnlyList<WorkingCluster> currentClusters,
        double toleranceMm)
    {
        var next = new List<WorkingCluster>();
        foreach (var item in items)
        {
            WorkingCluster? best = null;
            double bestDistance = double.MaxValue;

            foreach (var cluster in currentClusters)
            {
                if (!IsSameType(item.Dim1, item.Dim2, item.Dim3, cluster.Rep1, cluster.Rep2, cluster.Rep3, toleranceMm))
                    continue;

                double distance = Distance(item.Dim1, item.Dim2, item.Dim3, cluster.Rep1, cluster.Rep2, cluster.Rep3);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = cluster;
                }
            }

            if (best is null)
            {
                next.Add(new WorkingCluster(item));
                continue;
            }

            var existing = next.FirstOrDefault(cluster => ReferenceEquals(cluster.Source, best));
            if (existing is null)
            {
                existing = new WorkingCluster(best);
                next.Add(existing);
            }
            existing.Add(item);
        }

        return next;
    }

    private static void MergeClusters(List<WorkingCluster> clusters, double toleranceMm)
    {
        bool merged;
        do
        {
            merged = false;
            for (int i = 0; i < clusters.Count; i++)
            {
                for (int j = i + 1; j < clusters.Count; j++)
                {
                    var left = clusters[i];
                    var right = clusters[j];
                    if (!IsSameType(left.Rep1, left.Rep2, left.Rep3, right.Rep1, right.Rep2, right.Rep3, toleranceMm))
                        continue;

                    left.AddRange(right.Items);
                    clusters.RemoveAt(j);
                    merged = true;
                    break;
                }

                if (merged)
                    break;
            }
        } while (merged);
    }

    private static List<WorkingCluster> MergeBySnappedDimensions(IReadOnlyList<WorkingCluster> clusters)
    {
        var merged = new Dictionary<string, WorkingCluster>(StringComparer.Ordinal);
        foreach (var cluster in clusters)
        {
            double snapped1 = SnapToStep(cluster.Rep1, SnapStepMm);
            double snapped2 = SnapToStep(cluster.Rep2, SnapStepMm);
            double snapped3 = SnapToStep(cluster.Rep3, SnapStepMm);
            string key = $"{snapped1:F3}|{snapped2:F3}|{snapped3:F3}";

            if (!merged.TryGetValue(key, out var existing))
            {
                existing = new WorkingCluster(snapped1, snapped2, snapped3);
                merged[key] = existing;
            }

            existing.AddRange(cluster.Items);
            existing.OverrideRepresentative(snapped1, snapped2, snapped3);
        }

        return merged.Values.ToList();
    }

    private static string BuildClusterSignature(IReadOnlyList<WorkingCluster> clusters)
        => string.Join(
            ";",
            clusters
                .Select(cluster =>
                    $"{cluster.Count}:{Math.Round(cluster.Rep1, 3)}:{Math.Round(cluster.Rep2, 3)}:{Math.Round(cluster.Rep3, 3)}")
                .OrderBy(x => x, StringComparer.Ordinal));

    private static double Distance(
        double left1,
        double left2,
        double left3,
        double right1,
        double right2,
        double right3)
        => Math.Abs(left1 - right1) + Math.Abs(left2 - right2) + Math.Abs(left3 - right3);

    private static bool IsSameType(
        double left1,
        double left2,
        double left3,
        double right1,
        double right2,
        double right3,
        double toleranceMm)
        => Math.Abs(left1 - right1) <= toleranceMm
        && Math.Abs(left2 - right2) <= toleranceMm
        && Math.Abs(left3 - right3) <= toleranceMm;

    private static double[] NormalizeDimensions(double lengthMm, double heightMm, double depthMm)
    {
        var dims = new[] { lengthMm, heightMm, depthMm };
        Array.Sort(dims);
        Array.Reverse(dims);
        return dims;
    }

    private static double SnapToStep(double value, double step)
        => Math.Round(value / step, MidpointRounding.AwayFromZero) * step;

    private static BoxTypeClusterSummary ToCluster(WorkingCluster cluster)
    {
        return new BoxTypeClusterSummary
        {
            TypeId = string.Empty,
            Count = cluster.Count,
            RepLengthMm = Math.Round(cluster.Rep1, 3),
            RepHeightMm = Math.Round(cluster.Rep2, 3),
            RepDepthMm = Math.Round(cluster.Rep3, 3),
            WeightAvgKg = Math.Round(cluster.WeightSum / cluster.Count, 3),
            WeightMinKg = Math.Round(cluster.WeightMin, 3),
            WeightMaxKg = Math.Round(cluster.WeightMax, 3),
        };
    }

    private sealed class WorkingCluster
    {
        public WorkingCluster(ClassifiedItem seed)
        {
            Source = this;
            Add(seed);
        }

        public WorkingCluster(WorkingCluster source)
        {
            Source = source;
        }

        public WorkingCluster(double rep1, double rep2, double rep3)
        {
            Source = this;
            Rep1 = rep1;
            Rep2 = rep2;
            Rep3 = rep3;
        }

        public WorkingCluster Source { get; }
        public List<ClassifiedItem> Items { get; } = new();

        public int Count { get; private set; }
        public double Rep1 { get; private set; }
        public double Rep2 { get; private set; }
        public double Rep3 { get; private set; }
        public double WeightSum { get; private set; }
        public double WeightMin { get; private set; }
        public double WeightMax { get; private set; }

        public void Add(ClassifiedItem item)
        {
            Items.Add(item);
            Recalculate();
        }

        public void AddRange(IEnumerable<ClassifiedItem> items)
        {
            Items.AddRange(items);
            Recalculate();
        }

        public void OverrideRepresentative(double rep1, double rep2, double rep3)
        {
            Rep1 = rep1;
            Rep2 = rep2;
            Rep3 = rep3;
        }

        private void Recalculate()
        {
            Count = Items.Count;
            if (Count == 0)
            {
                WeightSum = 0;
                WeightMin = 0;
                WeightMax = 0;
                return;
            }

            Rep1 = Items.Average(x => x.Dim1);
            Rep2 = Items.Average(x => x.Dim2);
            Rep3 = Items.Average(x => x.Dim3);
            WeightSum = Items.Sum(x => x.Item.WeightKg);
            WeightMin = Items.Min(x => x.Item.WeightKg);
            WeightMax = Items.Max(x => x.Item.WeightKg);
        }
    }
}
