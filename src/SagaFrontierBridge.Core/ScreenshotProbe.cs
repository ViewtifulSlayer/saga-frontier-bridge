using System;
using System.Threading;
using System.Threading.Tasks;

namespace SagaFrontierBridge.Core
{
    public sealed class ScreenshotSnapshot
    {
        public bool Available { get; init; }

        public string? Error { get; init; }

        public byte[]? Png { get; init; }

        public int? Width { get; init; }

        public int? Height { get; init; }

        public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Captures a live game screenshot without writing a file or changing game state.
    /// Implementations must marshal the actual Unity capture to the main thread.
    /// </summary>
    public interface IScreenshotProbe
    {
        Task<ScreenshotSnapshot> CaptureAsync(CancellationToken cancellationToken);
    }

    public sealed class UnavailableScreenshotProbe : IScreenshotProbe
    {
        private readonly string _reason;

        public UnavailableScreenshotProbe(string reason)
        {
            _reason = string.IsNullOrWhiteSpace(reason)
                ? "The live screenshot probe is not configured."
                : reason;
        }

        public Task<ScreenshotSnapshot> CaptureAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new ScreenshotSnapshot
            {
                Available = false,
                Error = _reason,
                CapturedAtUtc = DateTimeOffset.UtcNow
            });
        }
    }
}
