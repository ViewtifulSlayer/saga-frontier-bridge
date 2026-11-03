using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SagaFrontierBridge.Mcp.Tests
{
    public sealed class McpServerTests
    {
        private const string InitializeRequest = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"bridge-tests","version":"1.0"}}}
            """;

        private const string InitializedNotification = """
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            """;

        private const string ToolsListRequest = """
            {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
            """;

        private const string ScreenshotRequest = """
            {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"bridge_screenshot","arguments":{}}}
            """;

        private const string MalformedToolsCallParams = """
            {"jsonrpc":"2.0","id":20,"method":"tools/call","params":[]}
            """;

        private const string WrongTypeToolArgument = """
            {"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"bridge_get_save","arguments":{"id":42}}}
            """;

        [Fact]
        public async Task InitializeListsToolsAndReturnsScreenshotImage()
        {
            using var bridge = new FakeBridgeServer();
            var repositoryRoot = FindRepositoryRoot();
            var mcpAssembly = FindMcpAssembly(repositoryRoot);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var process = StartMcp(mcpAssembly, repositoryRoot, bridge.Url);
            var token = timeout.Token;

            try
            {
                await WriteFrameAsync(
                    process.StandardInput.BaseStream,
                    InitializeRequest,
                    token);

                using var initialize = JsonDocument.Parse(
                    await ReadFrameAsync(process.StandardOutput.BaseStream, token));

                var initializeResult = initialize.RootElement.GetProperty("result");
                Assert.Equal(
                    "2024-11-05",
                    initializeResult.GetProperty("protocolVersion").GetString());
                Assert.Equal(
                    "saga-frontier-bridge-mcp",
                    initializeResult.GetProperty("serverInfo").GetProperty("name").GetString());
                Assert.Equal(
                    "0.1.0",
                    initializeResult.GetProperty("serverInfo").GetProperty("version").GetString());

                await WriteFrameAsync(
                    process.StandardInput.BaseStream,
                    InitializedNotification,
                    token);
                await WriteFrameAsync(
                    process.StandardInput.BaseStream,
                    ToolsListRequest,
                    token);

                using var toolList = JsonDocument.Parse(
                    await ReadFrameAsync(process.StandardOutput.BaseStream, token));

                var tools = toolList.RootElement
                    .GetProperty("result")
                    .GetProperty("tools")
                    .EnumerateArray()
                    .Select(tool => tool.GetProperty("name").GetString())
                    .ToArray();

                Assert.Equal(10, tools.Length);
                Assert.Contains("bridge_status", tools);
                Assert.Contains("bridge_screenshot", tools);

                await WriteFrameAsync(
                    process.StandardInput.BaseStream,
                    ScreenshotRequest,
                    token);

                using var toolCall = JsonDocument.Parse(
                    await ReadFrameAsync(process.StandardOutput.BaseStream, token));

                var result = toolCall.RootElement.GetProperty("result");
                Assert.False(result.GetProperty("isError").GetBoolean());

                var content = result.GetProperty("content").EnumerateArray().ToArray();
                var text = Assert.Single(
                    content,
                    item => string.Equals(
                        item.GetProperty("type").GetString(),
                        "text",
                        StringComparison.Ordinal));
                Assert.Contains(
                    "1x1 PNG",
                    text.GetProperty("text").GetString(),
                    StringComparison.Ordinal);

                var image = Assert.Single(
                    content,
                    item => string.Equals(
                        item.GetProperty("type").GetString(),
                        "image",
                        StringComparison.Ordinal));
                Assert.Equal("image/png", image.GetProperty("mimeType").GetString());

                var png = Convert.FromBase64String(
                    image.GetProperty("data").GetString() ?? string.Empty);
                Assert.Equal(FakeBridgeServer.Png, png);
            }
            finally
            {
                StopProcess(process);
            }
        }

        [Fact]
        public async Task MalformedFramingExitsWithFramingError()
        {
            using var bridge = new FakeBridgeServer();
            var repositoryRoot = FindRepositoryRoot();
            var mcpAssembly = FindMcpAssembly(repositoryRoot);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var process = StartMcp(mcpAssembly, repositoryRoot, bridge.Url);
            var token = timeout.Token;

            try
            {
                await WriteRawAsync(
                    process.StandardInput.BaseStream,
                    "not-a-frame\r\n\r\n",
                    token);
                process.StandardInput.Close();

                var stderr = await process.StandardError.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);

                Assert.Equal(1, process.ExitCode);
                Assert.Contains("framing error", stderr, StringComparison.Ordinal);
                Assert.Contains("Content-Length", stderr, StringComparison.Ordinal);
            }
            finally
            {
                StopProcess(process);
            }
        }

        [Fact]
        public async Task RequestsBeforeInitializeReturnNotInitializedError()
        {
            using var bridge = new FakeBridgeServer();
            var repositoryRoot = FindRepositoryRoot();
            var mcpAssembly = FindMcpAssembly(repositoryRoot);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var process = StartMcp(mcpAssembly, repositoryRoot, bridge.Url);
            var token = timeout.Token;

            try
            {
                await WriteFrameAsync(
                    process.StandardInput.BaseStream,
                    ToolsListRequest,
                    token);

                using var response = JsonDocument.Parse(
                    await ReadFrameAsync(process.StandardOutput.BaseStream, token));

                Assert.Equal(2, response.RootElement.GetProperty("id").GetInt32());
                var error = response.RootElement.GetProperty("error");
                Assert.Equal(-32002, error.GetProperty("code").GetInt32());
                Assert.Contains(
                    "not initialized",
                    error.GetProperty("message").GetString(),
                    StringComparison.Ordinal);
            }
            finally
            {
                StopProcess(process);
            }
        }

        [Fact]
        public async Task ScreenshotPropagatesBridge401AsToolError()
        {
            using var bridge = new FakeBridgeServer(FakeBridgeMode.Unauthorized);
            var repositoryRoot = FindRepositoryRoot();
            var mcpAssembly = FindMcpAssembly(repositoryRoot);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var process = StartMcp(mcpAssembly, repositoryRoot, bridge.Url);
            var token = timeout.Token;

            try
            {
                await InitializeSessionAsync(process, token);
                await WriteFrameAsync(
                    process.StandardInput.BaseStream,
                    ScreenshotRequest,
                    token);

                using var response = JsonDocument.Parse(
                    await ReadFrameAsync(process.StandardOutput.BaseStream, token));

                var result = response.RootElement.GetProperty("result");
                Assert.True(result.GetProperty("isError").GetBoolean());

                var content = result.GetProperty("content").EnumerateArray().ToArray();
                var text = Assert.Single(content);
                Assert.Equal("text", text.GetProperty("type").GetString());

                var message = text.GetProperty("text").GetString();
                Assert.Contains("HTTP 401", message, StringComparison.Ordinal);
                Assert.Contains(
                    "A valid bearer token is required.",
                    message,
                    StringComparison.Ordinal);

                Assert.DoesNotContain(
                    content,
                    item => string.Equals(
                        item.GetProperty("type").GetString(),
                        "image",
                        StringComparison.Ordinal));
                Assert.Equal("Bearer test-token", bridge.LastAuthorizationHeader);
            }
            finally
            {
                StopProcess(process);
            }
        }

        [Fact]
        public async Task ScreenshotReturnsNonImageErrorAsTextWithoutImageContent()
        {
            using var bridge = new FakeBridgeServer(FakeBridgeMode.ScreenshotUnavailable);
            var repositoryRoot = FindRepositoryRoot();
            var mcpAssembly = FindMcpAssembly(repositoryRoot);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var process = StartMcp(mcpAssembly, repositoryRoot, bridge.Url);
            var token = timeout.Token;

            try
            {
                await InitializeSessionAsync(process, token);
                await WriteFrameAsync(
                    process.StandardInput.BaseStream,
                    ScreenshotRequest,
                    token);

                using var response = JsonDocument.Parse(
                    await ReadFrameAsync(process.StandardOutput.BaseStream, token));

                var result = response.RootElement.GetProperty("result");
                Assert.True(result.GetProperty("isError").GetBoolean());

                var content = result.GetProperty("content").EnumerateArray().ToArray();
                var text = Assert.Single(content);
                Assert.Equal("text", text.GetProperty("type").GetString());

                var message = text.GetProperty("text").GetString();
                Assert.Contains("HTTP 503", message, StringComparison.Ordinal);
                Assert.Contains("screenshot_unavailable", message, StringComparison.Ordinal);

                Assert.DoesNotContain(
                    content,
                    item => string.Equals(
                        item.GetProperty("type").GetString(),
                        "image",
                        StringComparison.Ordinal));
            }
            finally
            {
                StopProcess(process);
            }
        }

        [Fact]
        public async Task NewlineDelimitedJsonFramingIsSupported()
        {
            using var bridge = new FakeBridgeServer();
            var repositoryRoot = FindRepositoryRoot();
            var mcpAssembly = FindMcpAssembly(repositoryRoot);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var process = StartMcp(mcpAssembly, repositoryRoot, bridge.Url);
            var token = timeout.Token;

            try
            {
                await WriteNewlineJsonAsync(
                    process.StandardInput.BaseStream,
                    InitializeRequest,
                    token);

                var initializeLine = await ReadNewlineJsonAsync(
                    process.StandardOutput.BaseStream,
                    token);

                Assert.StartsWith("{", initializeLine, StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "Content-Length:",
                    initializeLine,
                    StringComparison.OrdinalIgnoreCase);

                using var initialize = JsonDocument.Parse(initializeLine);
                Assert.Equal(1, initialize.RootElement.GetProperty("id").GetInt32());
                Assert.Equal(
                    "saga-frontier-bridge-mcp",
                    initialize.RootElement
                        .GetProperty("result")
                        .GetProperty("serverInfo")
                        .GetProperty("name")
                        .GetString());

                await WriteNewlineJsonAsync(
                    process.StandardInput.BaseStream,
                    InitializedNotification,
                    token);
                await WriteNewlineJsonAsync(
                    process.StandardInput.BaseStream,
                    ToolsListRequest,
                    token);

                var toolsLine = await ReadNewlineJsonAsync(
                    process.StandardOutput.BaseStream,
                    token);

                Assert.DoesNotContain(
                    "Content-Length:",
                    toolsLine,
                    StringComparison.OrdinalIgnoreCase);

                using var toolList = JsonDocument.Parse(toolsLine);
                Assert.Equal(
                    10,
                    toolList.RootElement
                        .GetProperty("result")
                        .GetProperty("tools")
                        .GetArrayLength());
            }
            finally
            {
                StopProcess(process);
            }
        }

        [Fact]
        public async Task InvalidJsonPayloadExitsWithFramingError()
        {
            using var bridge = new FakeBridgeServer();
            var repositoryRoot = FindRepositoryRoot();
            var mcpAssembly = FindMcpAssembly(repositoryRoot);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var process = StartMcp(mcpAssembly, repositoryRoot, bridge.Url);
            var token = timeout.Token;

            try
            {
                await WriteFrameAsync(
                    process.StandardInput.BaseStream,
                    "{\"jsonrpc\":\"2.0\",\"id\":1,",
                    token);
                process.StandardInput.Close();

                var stderr = await process.StandardError.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);

                Assert.Equal(1, process.ExitCode);
                Assert.Contains("framing error", stderr, StringComparison.Ordinal);
            }
            finally
            {
                StopProcess(process);
            }
        }

        [Fact]
        public async Task MalformedToolArgumentsReturnErrors()
        {
            using var bridge = new FakeBridgeServer();
            var repositoryRoot = FindRepositoryRoot();
            var mcpAssembly = FindMcpAssembly(repositoryRoot);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var process = StartMcp(mcpAssembly, repositoryRoot, bridge.Url);
            var token = timeout.Token;

            try
            {
                await InitializeSessionAsync(process, token);

                await WriteFrameAsync(
                    process.StandardInput.BaseStream,
                    MalformedToolsCallParams,
                    token);

                using var malformedParams = JsonDocument.Parse(
                    await ReadFrameAsync(process.StandardOutput.BaseStream, token));

                var error = malformedParams.RootElement.GetProperty("error");
                Assert.Equal(-32602, error.GetProperty("code").GetInt32());
                Assert.Equal(
                    "Invalid params for tools/call.",
                    error.GetProperty("message").GetString());

                await WriteFrameAsync(
                    process.StandardInput.BaseStream,
                    WrongTypeToolArgument,
                    token);

                using var toolResponse = JsonDocument.Parse(
                    await ReadFrameAsync(process.StandardOutput.BaseStream, token));

                var result = toolResponse.RootElement.GetProperty("result");
                Assert.True(result.GetProperty("isError").GetBoolean());

                var content = result.GetProperty("content").EnumerateArray().ToArray();
                var text = Assert.Single(content);
                Assert.Equal("text", text.GetProperty("type").GetString());
                Assert.Contains(
                    "Missing required string argument: id.",
                    text.GetProperty("text").GetString(),
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    content,
                    item => string.Equals(
                        item.GetProperty("type").GetString(),
                        "image",
                        StringComparison.Ordinal));
            }
            finally
            {
                StopProcess(process);
            }
        }

        [Fact]
        public async Task BridgeTimeoutReturnsToolErrorWithoutImage()
        {
            using var bridge = new FakeBridgeServer(FakeBridgeMode.Timeout);
            var repositoryRoot = FindRepositoryRoot();
            var mcpAssembly = FindMcpAssembly(repositoryRoot);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var process = StartMcp(mcpAssembly, repositoryRoot, bridge.Url);
            var token = timeout.Token;

            try
            {
                await InitializeSessionAsync(process, token);
                await WriteFrameAsync(
                    process.StandardInput.BaseStream,
                    ScreenshotRequest,
                    token);

                using var response = JsonDocument.Parse(
                    await ReadFrameAsync(process.StandardOutput.BaseStream, token));

                var result = response.RootElement.GetProperty("result");
                Assert.True(result.GetProperty("isError").GetBoolean());

                var content = result.GetProperty("content").EnumerateArray().ToArray();
                var text = Assert.Single(content);
                Assert.Equal("text", text.GetProperty("type").GetString());

                var message = text.GetProperty("text").GetString();
                Assert.StartsWith("Error:", message, StringComparison.Ordinal);
                Assert.Contains("Timeout", message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    content,
                    item => string.Equals(
                        item.GetProperty("type").GetString(),
                        "image",
                        StringComparison.Ordinal));
            }
            finally
            {
                StopProcess(process);
            }
        }

        private static async Task InitializeSessionAsync(
            Process process,
            CancellationToken cancellationToken)
        {
            await WriteFrameAsync(
                process.StandardInput.BaseStream,
                InitializeRequest,
                cancellationToken);

            using var initialize = JsonDocument.Parse(
                await ReadFrameAsync(process.StandardOutput.BaseStream, cancellationToken));
            Assert.True(initialize.RootElement.TryGetProperty("result", out _));

            await WriteFrameAsync(
                process.StandardInput.BaseStream,
                InitializedNotification,
                cancellationToken);
        }

        private static void StopProcess(Process process)
        {
            if (process.HasExited)
            {
                return;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }

        private static async Task WriteRawAsync(
            Stream stream,
            string text,
            CancellationToken cancellationToken)
        {
            var payload = Encoding.ASCII.GetBytes(text);
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private static async Task WriteNewlineJsonAsync(
            Stream stream,
            string json,
            CancellationToken cancellationToken)
        {
            var payload = Encoding.UTF8.GetBytes(json + "\n");
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private static async Task<string> ReadNewlineJsonAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            const int maxBodyBytes = 16 * 1024 * 1024;

            var body = new List<byte>();
            var singleByte = new byte[1];

            while (true)
            {
                var read = await stream.ReadAsync(
                    singleByte.AsMemory(0, 1),
                    cancellationToken);

                if (read == 0)
                {
                    throw new EndOfStreamException("The MCP process closed stdout.");
                }

                if (singleByte[0] == (byte)'\n')
                {
                    return Encoding.UTF8.GetString(body.ToArray()).TrimEnd('\r');
                }

                body.Add(singleByte[0]);

                if (body.Count > maxBodyBytes)
                {
                    throw new InvalidDataException(
                        "The newline-delimited MCP response was too large.");
                }
            }
        }

        private static Process StartMcp(
            string assemblyPath,
            string workingDirectory,
            string bridgeUrl)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add(assemblyPath);
            startInfo.Environment["SAGA_BRIDGE_URL"] = bridgeUrl;
            startInfo.Environment["SAGA_BRIDGE_TOKEN"] = "test-token";

            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("The MCP process could not be started.");
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SagaFrontierBridge.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                "Could not locate SagaFrontierBridge.sln above the test output directory.");
        }

        private static string FindMcpAssembly(string repositoryRoot)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name;
            var candidates = new[] { configuration, "Release", "Debug" }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                var path = Path.Combine(
                    repositoryRoot,
                    "src",
                    "SagaFrontierBridge.Mcp",
                    "bin",
                    candidate!,
                    "net8.0",
                    "SagaFrontierBridge.Mcp.dll");

                if (File.Exists(path))
                {
                    return path;
                }
            }

            throw new FileNotFoundException(
                "The MCP assembly was not found under src/SagaFrontierBridge.Mcp/bin.");
        }

        private static async Task WriteFrameAsync(
            Stream stream,
            string json,
            CancellationToken cancellationToken)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            var header = Encoding.ASCII.GetBytes(
                $"Content-Length: {payload.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");

            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private static async Task<byte[]> ReadFrameAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            const int maxHeaderBytes = 64 * 1024;
            const int maxBodyBytes = 16 * 1024 * 1024;

            var headerBytes = new List<byte>();
            var singleByte = new byte[1];

            while (true)
            {
                var read = await stream.ReadAsync(
                    singleByte.AsMemory(0, 1),
                    cancellationToken);

                if (read == 0)
                {
                    throw new EndOfStreamException("The MCP process closed stdout.");
                }

                headerBytes.Add(singleByte[0]);

                if (headerBytes.Count > maxHeaderBytes)
                {
                    throw new InvalidDataException("The MCP response header was too large.");
                }

                if (headerBytes.Count >= 4 &&
                    headerBytes[^4] == (byte)'\r' &&
                    headerBytes[^3] == (byte)'\n' &&
                    headerBytes[^2] == (byte)'\r' &&
                    headerBytes[^1] == (byte)'\n')
                {
                    break;
                }
            }

            var header = Encoding.ASCII.GetString(headerBytes.ToArray());
            var contentLength = -1;

            foreach (var line in header.Split(
                         "\r\n",
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                contentLength = int.Parse(
                    line.Substring("Content-Length:".Length).Trim(),
                    CultureInfo.InvariantCulture);
            }

            if (contentLength <= 0 || contentLength > maxBodyBytes)
            {
                throw new InvalidDataException(
                    "The MCP response did not contain a valid Content-Length header.");
            }

            var body = new byte[contentLength];
            await stream.ReadExactlyAsync(body, cancellationToken);
            return body;
        }

        private enum FakeBridgeMode
        {
            Png,
            Unauthorized,
            ScreenshotUnavailable,
            Timeout
        }

        private sealed class FakeBridgeServer : IDisposable
        {
            public static readonly byte[] Png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z4v8AAAAASUVORK5CYII=");

            private readonly FakeBridgeMode _mode;
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cancellation = new();
            private readonly Task _acceptLoop;

            public FakeBridgeServer(FakeBridgeMode mode = FakeBridgeMode.Png)
            {
                _mode = mode;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();

                var endpoint = (IPEndPoint)_listener.LocalEndpoint;
                Url = $"http://127.0.0.1:{endpoint.Port}";
                _acceptLoop = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
            }

            public string Url { get; }

            public string? LastAuthorizationHeader { get; private set; }

            public void Dispose()
            {
                _cancellation.Cancel();
                _listener.Stop();

                try
                {
                    _acceptLoop.Wait(TimeSpan.FromSeconds(2));
                }
                catch (AggregateException)
                {
                }

                _cancellation.Dispose();
            }

            private async Task AcceptLoopAsync(CancellationToken cancellationToken)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient client;

                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        break;
                    }

                    using (client)
                    {
                        await HandleRequestAsync(client, cancellationToken);
                    }
                }
            }

            private async Task HandleRequestAsync(
                TcpClient client,
                CancellationToken cancellationToken)
            {
                using var stream = client.GetStream();
                var header = await ReadRequestHeaderAsync(stream, cancellationToken);

                foreach (var line in header.Split(
                             "\r\n",
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    LastAuthorizationHeader = line
                        .Substring("Authorization:".Length)
                        .Trim();
                    break;
                }

                if (!header.Contains(
                        "GET /v1/screenshot HTTP/1.1",
                        StringComparison.Ordinal))
                {
                    var notFound = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(notFound, cancellationToken);
                    return;
                }

                if (_mode == FakeBridgeMode.Timeout)
                {
                    try
                    {
                        await Task.Delay(
                            System.Threading.Timeout.InfiniteTimeSpan,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    return;
                }

                string status;
                string contentType;
                byte[] body;
                bool isImage;

                switch (_mode)
                {
                    case FakeBridgeMode.Unauthorized:
                        status = "401 Unauthorized";
                        contentType = "application/json";
                        body = Encoding.UTF8.GetBytes(
                            "{\"error\":{\"code\":\"unauthorized\",\"message\":\"A valid bearer token is required.\"}}");
                        isImage = false;
                        break;

                    case FakeBridgeMode.ScreenshotUnavailable:
                        status = "503 Service Unavailable";
                        contentType = "application/json";
                        body = Encoding.UTF8.GetBytes(
                            "{\"error\":{\"code\":\"screenshot_unavailable\",\"message\":\"No frame is available.\"}}");
                        isImage = false;
                        break;

                    default:
                        status = "200 OK";
                        contentType = "image/png";
                        body = Png;
                        isImage = true;
                        break;
                }

                var responseHeader = new StringBuilder()
                    .Append($"HTTP/1.1 {status}\r\n")
                    .Append($"Content-Type: {contentType}\r\n")
                    .Append($"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n")
                    .Append("Cache-Control: no-store\r\n")
                    .Append("Connection: close\r\n");

                if (isImage)
                {
                    responseHeader.Append("X-Screenshot-Width: 1\r\n");
                    responseHeader.Append("X-Screenshot-Height: 1\r\n");
                }

                responseHeader.Append("\r\n");

                await stream.WriteAsync(
                    Encoding.ASCII.GetBytes(responseHeader.ToString()),
                    cancellationToken);
                await stream.WriteAsync(body, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            private static async Task<string> ReadRequestHeaderAsync(
                Stream stream,
                CancellationToken cancellationToken)
            {
                var bytes = new List<byte>();
                var singleByte = new byte[1];

                while (bytes.Count < 16 * 1024)
                {
                    var read = await stream.ReadAsync(
                        singleByte.AsMemory(0, 1),
                        cancellationToken);

                    if (read == 0)
                    {
                        break;
                    }

                    bytes.Add(singleByte[0]);

                    if (bytes.Count >= 4 &&
                        bytes[^4] == (byte)'\r' &&
                        bytes[^3] == (byte)'\n' &&
                        bytes[^2] == (byte)'\r' &&
                        bytes[^1] == (byte)'\n')
                    {
                        break;
                    }
                }

                return Encoding.ASCII.GetString(bytes.ToArray());
            }
        }
    }
}
