using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using SagaFrontierBridge.Core;

namespace SagaFrontierBridge.Mcp
{
    public sealed class BridgeClientOptions
    {
        public Uri BaseAddress { get; }

        public string? AccessToken { get; }

        public BridgeClientOptions(Uri baseAddress, string? accessToken = null)
        {
            if (baseAddress == null)
            {
                throw new ArgumentNullException(nameof(baseAddress));
            }

            if (!baseAddress.IsAbsoluteUri || baseAddress.Scheme != Uri.UriSchemeHttp)
            {
                throw new ArgumentException(
                    "The bridge URL must be an absolute http:// URL.",
                    nameof(baseAddress));
            }

            if (!string.Equals(baseAddress.Host, "127.0.0.1", StringComparison.Ordinal) &&
                !string.Equals(baseAddress.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "The bridge URL must point at 127.0.0.1 or localhost.",
                    nameof(baseAddress));
            }

            if (baseAddress.AbsolutePath != "/")
            {
                throw new ArgumentException(
                    "The bridge URL must not contain a path.",
                    nameof(baseAddress));
            }

            var builder = new UriBuilder(baseAddress)
            {
                Host = "127.0.0.1",
                Path = "/",
                Query = string.Empty,
                Fragment = string.Empty
            };

            if (baseAddress.IsDefaultPort)
            {
                builder.Port = BridgeOptions.DefaultPort;
            }

            if (builder.Port < 1024 || builder.Port > 65535)
            {
                throw new ArgumentException(
                    "The bridge port must be between 1024 and 65535.",
                    nameof(baseAddress));
            }

            BaseAddress = builder.Uri;
            AccessToken = string.IsNullOrWhiteSpace(accessToken)
                ? null
                : accessToken.Trim();
        }

        public static BridgeClientOptions FromEnvironment()
        {
            var rawUrl = Environment.GetEnvironmentVariable("SAGA_BRIDGE_URL");
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                rawUrl = $"http://127.0.0.1:{BridgeOptions.DefaultPort}";
            }

            if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var baseAddress))
            {
                throw new InvalidOperationException(
                    "SAGA_BRIDGE_URL is not a valid absolute URL.");
            }

            return new BridgeClientOptions(
                baseAddress,
                Environment.GetEnvironmentVariable("SAGA_BRIDGE_TOKEN"));
        }
    }

    public sealed record BridgeResponse(int StatusCode, bool IsSuccess, string Body);

    public sealed record BridgeBinaryResponse(
        int StatusCode,
        bool IsSuccess,
        byte[] Body,
        string? ContentType,
        IReadOnlyDictionary<string, string> Headers);

    public sealed class BridgeClient : IDisposable
    {
        private readonly HttpClient _http;

        public BridgeClient(BridgeClientOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var handler = new HttpClientHandler
            {
                UseProxy = false
            };

            _http = new HttpClient(handler)
            {
                BaseAddress = options.BaseAddress,
                Timeout = TimeSpan.FromSeconds(10)
            };

            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            if (options.AccessToken != null)
            {
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", options.AccessToken);
            }
        }

        public async Task<BridgeResponse> GetAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("A request path is required.", nameof(relativePath));
            }

            var path = relativePath.TrimStart('/');
            using var response = await _http.GetAsync(
                path,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            var body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            return new BridgeResponse(
                (int)response.StatusCode,
                response.IsSuccessStatusCode,
                body);
        }

        public async Task<BridgeBinaryResponse> GetBytesAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("A request path is required.", nameof(relativePath));
            }

            var path = relativePath.TrimStart('/');
            using var response = await _http.GetAsync(
                path,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            var body = await response.Content
                .ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            foreach (var header in response.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            return new BridgeBinaryResponse(
                (int)response.StatusCode,
                response.IsSuccessStatusCode,
                body,
                response.Content.Headers.ContentType?.MediaType,
                headers);
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
