using System;

namespace SagaFrontierBridge.Core
{
    public sealed class FlagSnapshot
    {
        public bool Available { get; init; }

        public string? Error { get; init; }

        public string? Source { get; init; }

        public bool? SaveLoaded { get; init; }

        public int[]? ScenarioFlags { get; init; }

        public int[]? BoxFlags { get; init; }

        public int[]? ScenarioChartState { get; init; }

        public int[]? EventFlags { get; init; }

        public int[]? EventTemp { get; init; }

        public int[]? EventBattle { get; init; }

        public int[]? Cfg1 { get; init; }

        public int? SetScenarioFlagCount { get; init; }

        public int? SetScenarioChartCount { get; init; }

        public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }
}
