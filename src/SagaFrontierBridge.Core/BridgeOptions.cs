using System;

namespace SagaFrontierBridge.Core
{
    public sealed class BridgeOptions
    {
        public const int DefaultPort = 27999;

        public int Port { get; init; } = DefaultPort;

        public string? AccessToken { get; init; }

        public string? SaveRoot { get; init; }

        public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

        public BridgeOptions Validate()
        {
            if (Port < 1024 || Port > 65535)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Port),
                    "Port must be between 1024 and 65535.");
            }

            if (RequestTimeout < TimeSpan.FromSeconds(1) || RequestTimeout > TimeSpan.FromMinutes(1))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(RequestTimeout),
                    "RequestTimeout must be between 1 second and 1 minute.");
            }

            return this;
        }
    }
}
