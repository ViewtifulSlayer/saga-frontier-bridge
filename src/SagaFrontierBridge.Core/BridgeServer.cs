using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SagaFrontierBridge.Core
{
    public sealed class BridgeServer : IDisposable
    {
        private readonly BridgeOptions _options;
        private readonly IRuntimeProbe _runtimeProbe;
        private readonly ISaveWorkProbe _saveWorkProbe;
        private readonly IScreenshotProbe _screenshotProbe;
        private readonly SaveCatalog _saveCatalog;
        private readonly Action<string>? _log;
        private readonly string? _accessToken;
        private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;

        private TcpListener? _listener;
        private CancellationTokenSource? _cancellation;
        private Task? _acceptLoop;
        private int _disposed;

        public BridgeServer(BridgeOptions options, IRuntimeProbe runtimeProbe, Action<string>? log = null)
            : this(
                options,
                runtimeProbe,
                new UnavailableSaveWorkProbe(
                    "The live SAVE_WORK probe is not configured."),
                new UnavailableScreenshotProbe(
                    "The live screenshot probe is not configured."),
                log)
        {
        }

        public BridgeServer(
            BridgeOptions options,
            IRuntimeProbe runtimeProbe,
            ISaveWorkProbe saveWorkProbe,
            Action<string>? log = null)
            : this(
                options,
                runtimeProbe,
                saveWorkProbe,
                new UnavailableScreenshotProbe(
                    "The live screenshot probe is not configured."),
                log)
        {
        }

        public BridgeServer(
            BridgeOptions options,
            IRuntimeProbe runtimeProbe,
            ISaveWorkProbe saveWorkProbe,
            IScreenshotProbe screenshotProbe,
            Action<string>? log = null)
        {
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
            _runtimeProbe = runtimeProbe ?? throw new ArgumentNullException(nameof(runtimeProbe));
            _saveWorkProbe = saveWorkProbe ?? throw new ArgumentNullException(nameof(saveWorkProbe));
            _screenshotProbe = screenshotProbe ?? throw new ArgumentNullException(nameof(screenshotProbe));
            _saveCatalog = new SaveCatalog(_options.SaveRoot);
            _accessToken = string.IsNullOrWhiteSpace(_options.AccessToken)
                ? null
                : _options.AccessToken.Trim();
            _log = log;
        }

        public int Port => _options.Port;

        public string Url => $"http://127.0.0.1:{_options.Port}/";

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(BridgeServer));
            }

            if (_listener != null)
            {
                throw new InvalidOperationException("The bridge is already running.");
            }

            var listener = new TcpListener(IPAddress.Loopback, _options.Port);
            listener.Start();

            _listener = listener;
            _cancellation = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cancellation.Token));
            Log($"Listening on {Url}");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _listener?.Stop();
            }
            catch (SocketException)
            {
            }

            try
            {
                _acceptLoop?.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException)
            {
            }

            _cancellation?.Dispose();
            _cancellation = null;
            _listener = null;
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    Log($"Accept failed: {ex.SocketErrorCode}");
                    continue;
                }

                _ = HandleClientAsync(client, cancellationToken);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;

                    if (client.Client.RemoteEndPoint is not IPEndPoint remote ||
                        !IPAddress.IsLoopback(remote.Address))
                    {
                        Log("Rejected a non-loopback client.");
                        return;
                    }

                    using var stream = client.GetStream();
                    using var requestCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    requestCancellation.CancelAfter(_options.RequestTimeout);

                    BridgeHttpRequest? request;

                    try
                    {
                        request = await ReadRequestAsync(stream, requestCancellation.Token)
                            .ConfigureAwait(false);
                    }
                    catch (BridgeProtocolException ex)
                    {
                        var error = ErrorResponse(ex.StatusCode, ex.Code, ex.Message);
                        await WriteResponseAsync(stream, error, false, requestCancellation.Token)
                            .ConfigureAwait(false);
                        Log($"{ex.StatusCode} {ex.Code}");
                        return;
                    }

                    if (request == null)
                    {
                        return;
                    }

                    BridgeHttpResponse response;

                    try
                    {
                        response = await RouteAsync(request, requestCancellation.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log($"Route failed: {ex.GetType().Name}: {ex.Message}");
                        response = ErrorResponse(
                            500,
                            "internal_error",
                            "The bridge could not build a response.");
                    }

                    await WriteResponseAsync(
                            stream,
                            response,
                            string.Equals(request.Method, "HEAD", StringComparison.Ordinal),
                            requestCancellation.Token)
                        .ConfigureAwait(false);

                    Log($"{request.Method} {request.Path} -> {response.StatusCode}");
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                catch (Exception ex)
                {
                    Log($"Client failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private async Task<BridgeHttpRequest> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var requestLine = await ReadLineAsync(stream, 4096, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(requestLine))
            {
                throw new BridgeProtocolException(
                    400,
                    "bad_request",
                    "An HTTP request line is required.");
            }

            var parts = requestLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
            {
                throw new BridgeProtocolException(
                    400,
                    "bad_request",
                    "The HTTP request line is malformed.");
            }

            var method = parts[0].ToUpperInvariant();

            if (!string.Equals(parts[2], "HTTP/1.1", StringComparison.Ordinal) &&
                !string.Equals(parts[2], "HTTP/1.0", StringComparison.Ordinal))
            {
                throw new BridgeProtocolException(
                    400,
                    "bad_request",
                    "Only HTTP/1.0 and HTTP/1.1 are supported.");
            }

            var target = parts[1];
            if (!target.StartsWith("/", StringComparison.Ordinal))
            {
                throw new BridgeProtocolException(
                    400,
                    "bad_request",
                    "Only origin-form request targets are supported.");
            }

            var queryIndex = target.IndexOf('?');
            var path = queryIndex >= 0 ? target.Substring(0, queryIndex) : target;
            if (path.Length == 0)
            {
                path = "/";
            }

            if (path.Length > 512 || path.Contains("..", StringComparison.Ordinal))
            {
                throw new BridgeProtocolException(
                    400,
                    "bad_request",
                    "The request path is not valid.");
            }

            var headers = await ReadHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
            ValidateHost(headers);
            ValidateToken(headers);

            if (!string.Equals(method, "GET", StringComparison.Ordinal) &&
                !string.Equals(method, "HEAD", StringComparison.Ordinal))
            {
                if (!headers.ContainsKey("Transfer-Encoding"))
                {
                    await DrainBodyAsync(stream, GetContentLength(headers), cancellationToken)
                        .ConfigureAwait(false);
                }

                throw new BridgeProtocolException(
                    405,
                    "method_not_allowed",
                    "This bridge is read-only; only GET and HEAD are allowed.");
            }

            ValidateNoBody(headers);

            return new BridgeHttpRequest(method, path);
        }

        private static async Task<Dictionary<string, string>> ReadHeadersAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var totalLength = 0;

            while (true)
            {
                var line = await ReadLineAsync(stream, 8192, cancellationToken)
                    .ConfigureAwait(false);

                if (line == null)
                {
                    throw new BridgeProtocolException(
                        400,
                        "bad_request",
                        "The HTTP headers ended unexpectedly.");
                }

                if (line.Length == 0)
                {
                    break;
                }

                totalLength += line.Length + 2;
                if (totalLength > 8192)
                {
                    throw new BridgeProtocolException(
                        400,
                        "request_too_large",
                        "The HTTP headers are too large.");
                }

                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    throw new BridgeProtocolException(
                        400,
                        "bad_request",
                        "An HTTP header is malformed.");
                }

                var name = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();

                if (name.Length == 0 || value.Length == 0)
                {
                    throw new BridgeProtocolException(
                        400,
                        "bad_request",
                        "An HTTP header is empty.");
                }

                if (headers.ContainsKey(name))
                {
                    throw new BridgeProtocolException(
                        400,
                        "bad_request",
                        "Duplicate HTTP headers are not supported.");
                }

                headers[name] = value;

                if (headers.Count > 64)
                {
                    throw new BridgeProtocolException(
                        400,
                        "request_too_large",
                        "Too many HTTP headers.");
                }
            }

            return headers;
        }

        private void ValidateHost(Dictionary<string, string> headers)
        {
            if (!headers.TryGetValue("Host", out var hostHeader) ||
                string.IsNullOrWhiteSpace(hostHeader))
            {
                throw new BridgeProtocolException(
                    400,
                    "bad_request",
                    "A Host header is required.");
            }

            var host = hostHeader.Trim();
            string hostName;
            int? hostPort = null;

            var separator = host.LastIndexOf(':');
            if (separator >= 0)
            {
                hostName = host.Substring(0, separator);
                var portText = host.Substring(separator + 1);

                if (!int.TryParse(
                        portText,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var parsedPort))
                {
                    throw new BridgeProtocolException(
                        400,
                        "bad_request",
                        "The Host header port is invalid.");
                }

                hostPort = parsedPort;
            }
            else
            {
                hostName = host;
            }

            if (hostPort.HasValue && hostPort.Value != _options.Port)
            {
                throw new BridgeProtocolException(
                    400,
                    "bad_request",
                    "The Host header port does not match the bridge port.");
            }

            if (!string.Equals(hostName, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(hostName, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeProtocolException(
                    400,
                    "bad_request",
                    "The Host header must name the loopback interface.");
            }
        }

        private static void ValidateNoBody(Dictionary<string, string> headers)
        {
            if (GetContentLength(headers) is > 0)
            {
                throw new BridgeProtocolException(
                    400,
                    "bad_request",
                    "Request bodies are not accepted.");
            }

            if (headers.ContainsKey("Transfer-Encoding"))
            {
                throw new BridgeProtocolException(
                    400,
                    "bad_request",
                    "Request bodies are not accepted.");
            }
        }

        private static long? GetContentLength(Dictionary<string, string> headers)
        {
            if (!headers.TryGetValue("Content-Length", out var contentLength))
            {
                return null;
            }

            if (!long.TryParse(
                    contentLength,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsed) || parsed < 0)
            {
                throw new BridgeProtocolException(
                    400,
                    "bad_request",
                    "The Content-Length header is invalid.");
            }

            return parsed;
        }

        private static async Task DrainBodyAsync(
            NetworkStream stream,
            long? contentLength,
            CancellationToken cancellationToken)
        {
            if (!contentLength.HasValue || contentLength.Value <= 0)
            {
                return;
            }

            if (contentLength.Value > 65536)
            {
                return;
            }

            var buffer = new byte[4096];
            var remaining = contentLength.Value;

            while (remaining > 0)
            {
                var read = await stream.ReadAsync(
                        buffer,
                        0,
                        (int)Math.Min(buffer.Length, remaining),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                remaining -= read;
            }
        }

        private void ValidateToken(Dictionary<string, string> headers)
        {
            if (_accessToken == null)
            {
                return;
            }

            string? supplied = null;

            if (headers.TryGetValue("Authorization", out var authorization) &&
                authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                supplied = authorization.Substring("Bearer ".Length).Trim();
            }
            else if (headers.TryGetValue("X-Bridge-Token", out var token))
            {
                supplied = token.Trim();
            }

            if (!FixedTimeEquals(supplied, _accessToken))
            {
                throw new BridgeProtocolException(
                    401,
                    "unauthorized",
                    "A valid bearer token is required.");
            }
        }

        private static bool FixedTimeEquals(string? supplied, string expected)
        {
            var suppliedBytes = Encoding.UTF8.GetBytes(supplied ?? string.Empty);
            var expectedBytes = Encoding.UTF8.GetBytes(expected);

            if (suppliedBytes.Length != expectedBytes.Length)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
        }

        private Task<BridgeHttpResponse> RouteAsync(
            BridgeHttpRequest request,
            CancellationToken cancellationToken)
        {
            if (string.Equals(request.Path, "/v1/screenshot", StringComparison.Ordinal))
            {
                return CaptureScreenshotAsync(cancellationToken);
            }

            return Task.FromResult(Route(request));
        }

        private async Task<BridgeHttpResponse> CaptureScreenshotAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                var screenshot = await _screenshotProbe
                    .CaptureAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (screenshot == null || !screenshot.Available || screenshot.Png == null)
                {
                    return ErrorResponse(
                        503,
                        "screenshot_unavailable",
                        screenshot?.Error ?? "The screenshot probe returned no image.");
                }

                var headers = new Dictionary<string, string>();
                if (screenshot.Width.HasValue)
                {
                    headers["X-Screenshot-Width"] = screenshot.Width.Value.ToString(
                        CultureInfo.InvariantCulture);
                }

                if (screenshot.Height.HasValue)
                {
                    headers["X-Screenshot-Height"] = screenshot.Height.Value.ToString(
                        CultureInfo.InvariantCulture);
                }

                return new BridgeHttpResponse(
                    200,
                    "image/png",
                    screenshot.Png,
                    headers);
            }
            catch (OperationCanceledException)
            {
                return ErrorResponse(
                    503,
                    "screenshot_timeout",
                    "The screenshot capture did not finish before the request timeout.");
            }
            catch (Exception ex)
            {
                return ErrorResponse(
                    503,
                    "screenshot_unavailable",
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private BridgeHttpResponse Route(BridgeHttpRequest request)
        {
            if (string.Equals(request.Path, "/v1/health", StringComparison.Ordinal))
            {
                return JsonResponse(200, BuildHealth());
            }

            if (string.Equals(request.Path, "/v1/runtime", StringComparison.Ordinal))
            {
                return JsonResponse(200, CaptureRuntime());
            }

            if (string.Equals(request.Path, "/v1/save-work", StringComparison.Ordinal))
            {
                return JsonResponse(200, CaptureSaveWork());
            }

            if (string.Equals(request.Path, "/v1/party", StringComparison.Ordinal))
            {
                return JsonResponse(200, CaptureParty());
            }

            if (string.Equals(request.Path, "/v1/flags", StringComparison.Ordinal))
            {
                return JsonResponse(200, CaptureFlags());
            }

            if (string.Equals(request.Path, "/v1/state", StringComparison.Ordinal))
            {
                return JsonResponse(200, CaptureState());
            }

            if (string.Equals(request.Path, "/v1/saves", StringComparison.Ordinal))
            {
                return JsonResponse(200, _saveCatalog.Discover());
            }

            if (string.Equals(request.Path, "/v1/capabilities", StringComparison.Ordinal))
            {
                return JsonResponse(200, BuildCapabilities());
            }

            const string savePrefix = "/v1/saves/";
            if (request.Path.StartsWith(savePrefix, StringComparison.Ordinal))
            {
                var id = request.Path.Substring(savePrefix.Length);
                if (id.Length == 0 || id.Contains('/'))
                {
                    return ErrorResponse(404, "not_found", "The save id was not found.");
                }

                var save = _saveCatalog.Find(id);
                if (save == null)
                {
                    return ErrorResponse(404, "not_found", "The save id was not found.");
                }

                return JsonResponse(200, save);
            }

            return ErrorResponse(404, "not_found", "The endpoint was not found.");
        }

        private object BuildHealth()
        {
            var saves = _saveCatalog.Discover();
            var runtime = CaptureRuntime();

            return new
            {
                status = "ok",
                bridge = BridgeInfo.Name,
                version = BridgeInfo.Version,
                readOnly = true,
                pid = Environment.ProcessId,
                startedAtUtc = _startedAtUtc,
                uptimeSeconds = Math.Round(
                    (DateTimeOffset.UtcNow - _startedAtUtc).TotalSeconds,
                    3),
                saveRoot = saves.Root,
                saveRootExists = saves.RootExists,
                saveCount = saves.Saves.Count,
                runtimeAvailable = runtime.Available
            };
        }

        private object BuildCapabilities()
        {
            return new
            {
                readOnly = true,
                methods = new[] { "GET", "HEAD" },
                endpoints = new[]
                {
                    "/v1/health",
                    "/v1/runtime",
                    "/v1/save-work",
                    "/v1/party",
                    "/v1/flags",
                    "/v1/state",
                    "/v1/screenshot",
                    "/v1/saves",
                    "/v1/saves/{id}",
                    "/v1/capabilities"
                },
                planned = Array.Empty<string>(),
                requestBodies = false,
                fileContents = false
            };
        }

        private SaveWorkSnapshot CaptureSaveWork()
        {
            try
            {
                return _saveWorkProbe.Capture() ?? new SaveWorkSnapshot
                {
                    Available = false,
                    Error = "The live SAVE_WORK probe returned no snapshot."
                };
            }
            catch (Exception ex)
            {
                return new SaveWorkSnapshot
                {
                    Available = false,
                    Error = $"{ex.GetType().Name}: {ex.Message}",
                    CapturedAtUtc = DateTimeOffset.UtcNow
                };
            }
        }

        private PartySnapshot CaptureParty()
        {
            try
            {
                return _saveWorkProbe.CaptureParty() ?? new PartySnapshot
                {
                    Available = false,
                    Error = "The live party probe returned no snapshot."
                };
            }
            catch (Exception ex)
            {
                return new PartySnapshot
                {
                    Available = false,
                    Error = $"{ex.GetType().Name}: {ex.Message}",
                    CapturedAtUtc = DateTimeOffset.UtcNow
                };
            }
        }

        private StateSnapshot CaptureState()
        {
            try
            {
                return _saveWorkProbe.CaptureState() ?? new StateSnapshot
                {
                    Available = false,
                    Error = "The live state probe returned no snapshot."
                };
            }
            catch (Exception ex)
            {
                return new StateSnapshot
                {
                    Available = false,
                    Error = $"{ex.GetType().Name}: {ex.Message}",
                    CapturedAtUtc = DateTimeOffset.UtcNow
                };
            }
        }

        private FlagSnapshot CaptureFlags()
        {
            try
            {
                return _saveWorkProbe.CaptureFlags() ?? new FlagSnapshot
                {
                    Available = false,
                    Error = "The live flag probe returned no snapshot."
                };
            }
            catch (Exception ex)
            {
                return new FlagSnapshot
                {
                    Available = false,
                    Error = $"{ex.GetType().Name}: {ex.Message}",
                    CapturedAtUtc = DateTimeOffset.UtcNow
                };
            }
        }

        private RuntimeSnapshot CaptureRuntime()
        {
            try
            {
                return _runtimeProbe.Capture() ?? new RuntimeSnapshot
                {
                    Available = false,
                    Error = "The runtime probe returned no snapshot."
                };
            }
            catch (Exception ex)
            {
                return new RuntimeSnapshot
                {
                    Available = false,
                    Error = $"{ex.GetType().Name}: {ex.Message}",
                    CapturedAtUtc = DateTimeOffset.UtcNow
                };
            }
        }

        private static BridgeHttpResponse JsonResponse(int statusCode, object value)
        {
            return new BridgeHttpResponse(
                statusCode,
                "application/json; charset=utf-8",
                BridgeJson.Serialize(value));
        }

        private static BridgeHttpResponse ErrorResponse(int statusCode, string code, string message)
        {
            return JsonResponse(statusCode, new
            {
                error = new
                {
                    code,
                    message
                }
            });
        }

        private static async Task WriteResponseAsync(
            NetworkStream stream,
            BridgeHttpResponse response,
            bool headOnly,
            CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            builder.Append("HTTP/1.1 ")
                .Append(response.StatusCode)
                .Append(' ')
                .Append(ReasonPhrase(response.StatusCode))
                .Append("\r\n");
            builder.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
            builder.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
            builder.Append("Cache-Control: no-store\r\n");
            builder.Append("X-Content-Type-Options: nosniff\r\n");
            builder.Append("Connection: close\r\n");
            builder.Append("Server: ")
                .Append(BridgeInfo.Name)
                .Append('/')
                .Append(BridgeInfo.Version)
                .Append("\r\n");

            if (response.Headers != null)
            {
                foreach (var header in response.Headers)
                {
                    builder.Append(header.Key)
                        .Append(": ")
                        .Append(header.Value)
                        .Append("\r\n");
                }
            }

            builder.Append("\r\n");

            var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
            await stream.WriteAsync(
                    headerBytes,
                    0,
                    headerBytes.Length,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!headOnly && response.Body.Length > 0)
            {
                await stream.WriteAsync(
                        response.Body,
                        0,
                        response.Body.Length,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static string ReasonPhrase(int statusCode)
        {
            return statusCode switch
            {
                200 => "OK",
                400 => "Bad Request",
                401 => "Unauthorized",
                404 => "Not Found",
                405 => "Method Not Allowed",
                500 => "Internal Server Error",
                503 => "Service Unavailable",
                _ => "Error"
            };
        }

        private static async Task<string?> ReadLineAsync(
            NetworkStream stream,
            int maxBytes,
            CancellationToken cancellationToken)
        {
            var bytes = new List<byte>(64);
            var buffer = new byte[1];

            while (bytes.Count <= maxBytes)
            {
                var read = await stream.ReadAsync(
                        buffer,
                        0,
                        1,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    if (bytes.Count == 0)
                    {
                        return null;
                    }

                    break;
                }

                if (buffer[0] == (byte)'\n')
                {
                    break;
                }

                bytes.Add(buffer[0]);
            }

            if (bytes.Count > maxBytes)
            {
                throw new BridgeProtocolException(
                    400,
                    "request_too_large",
                    "An HTTP line is too large.");
            }

            if (bytes.Count > 0 && bytes[bytes.Count - 1] == (byte)'\r')
            {
                bytes.RemoveAt(bytes.Count - 1);
            }

            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private void Log(string message)
        {
            try
            {
                _log?.Invoke(message);
            }
            catch
            {
                // Logging must never take down the bridge.
            }
        }

        private sealed class BridgeHttpRequest
        {
            public BridgeHttpRequest(string method, string path)
            {
                Method = method;
                Path = path;
            }

            public string Method { get; }

            public string Path { get; }
        }

        private sealed class BridgeHttpResponse
        {
            public BridgeHttpResponse(
                int statusCode,
                string contentType,
                byte[] body,
                IReadOnlyDictionary<string, string>? headers = null)
            {
                StatusCode = statusCode;
                ContentType = contentType;
                Body = body;
                Headers = headers;
            }

            public int StatusCode { get; }

            public string ContentType { get; }

            public byte[] Body { get; }

            public IReadOnlyDictionary<string, string>? Headers { get; }
        }

        private sealed class BridgeProtocolException : Exception
        {
            public BridgeProtocolException(int statusCode, string code, string message)
                : base(message)
            {
                StatusCode = statusCode;
                Code = code;
            }

            public int StatusCode { get; }

            public string Code { get; }
        }
    }
}
