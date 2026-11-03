using System;

namespace SagaFrontierBridge.Core
{
    public sealed class StateSnapshot
    {
        public bool Available { get; init; }

        public string? Error { get; init; }

        public string? Source { get; init; }

        public bool? SaveLoaded { get; init; }

        public string? ImfName { get; init; }

        public int? MapId { get; init; }

        public int? AreaId { get; init; }

        public int? RegionId { get; init; }

        public int? X { get; init; }

        public int? Z { get; init; }

        public int? Direction { get; init; }

        public int? PreviousMapId { get; init; }

        public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }
}
