using System;

namespace SagaFrontierBridge.Core
{
    public interface IRuntimeProbe
    {
        RuntimeSnapshot Capture();
    }

    public sealed class RuntimeSnapshot
    {
        public bool Available { get; init; }

        public string? UnityVersion { get; init; }

        public string? GameVersion { get; init; }

        public string? ProductName { get; init; }

        public string? CompanyName { get; init; }

        public RuntimeScene? Scene { get; init; }

        public RuntimeTime? Time { get; init; }

        public string? Error { get; init; }

        public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }

    public sealed class RuntimeScene
    {
        public string? Name { get; init; }

        public string? Path { get; init; }

        public int? BuildIndex { get; init; }

        public bool? IsLoaded { get; init; }
    }

    public sealed class RuntimeTime
    {
        public long? FrameCount { get; init; }

        public double? RealtimeSinceStartup { get; init; }

        public double? TimeScale { get; init; }
    }

    public sealed class UnavailableRuntimeProbe : IRuntimeProbe
    {
        private readonly string _reason;

        public UnavailableRuntimeProbe(string reason)
        {
            _reason = reason;
        }

        public RuntimeSnapshot Capture()
        {
            return new RuntimeSnapshot
            {
                Available = false,
                Error = _reason
            };
        }
    }
}
