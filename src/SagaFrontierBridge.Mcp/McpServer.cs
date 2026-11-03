using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SagaFrontierBridge.Core;

namespace SagaFrontierBridge.Mcp
{
    internal sealed class McpServer
    {
        private const string ProtocolVersion = "2024-11-05";

        private readonly StdioTransport _transport;
        private readonly IReadOnlyList<ToolDefinition> _tools;
        private bool _initialized;

        public McpServer(StdioTransport transport, BridgeClient bridge)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _tools = McpTools.Create(bridge);
        }

        public async Task<int> RunAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                JsonDocument? document;

                try
                {
                    document = await _transport.ReadAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"saga-frontier-bridge-mcp: framing error: {ex.Message}");
                    return 1;
                }

                if (document == null)
                {
                    return 0;
                }

                using (document)
                {
                    try
                    {
                        await ProcessAsync(document.RootElement, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"saga-frontier-bridge-mcp: message error: {ex.Message}");
                    }
                }
            }
        }

        private async Task ProcessAsync(
            JsonElement message,
            CancellationToken cancellationToken)
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!message.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var method = methodElement.GetString() ?? string.Empty;

            if (!message.TryGetProperty("id", out var id))
            {
                HandleNotification(method);
                return;
            }

            var parameters = message.TryGetProperty("params", out var paramsElement)
                ? paramsElement
                : default;

            switch (method)
            {
                case "initialize":
                    _initialized = true;
                    await SendResultAsync(
                        id,
                        new JsonObject
                        {
                            ["protocolVersion"] = ProtocolVersion,
                            ["capabilities"] = new JsonObject
                            {
                                ["tools"] = new JsonObject()
                            },
                            ["serverInfo"] = new JsonObject
                            {
                                ["name"] = "saga-frontier-bridge-mcp",
                                ["version"] = BridgeInfo.Version
                            }
                        },
                        cancellationToken).ConfigureAwait(false);
                    break;

                case "tools/list":
                    if (!await EnsureInitializedAsync(id, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        return;
                    }

                    await SendResultAsync(
                        id,
                        new JsonObject
                        {
                            ["tools"] = BuildToolList()
                        },
                        cancellationToken).ConfigureAwait(false);
                    break;

                case "tools/call":
                    if (!await EnsureInitializedAsync(id, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        return;
                    }

                    await HandleToolCallAsync(id, parameters, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case "ping":
                    await SendResultAsync(
                        id,
                        new JsonObject(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    await SendErrorAsync(
                        id,
                        -32601,
                        $"Method not found: {method}",
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        private static void HandleNotification(string method)
        {
            // MCP clients send notifications/initialized after the handshake and
            // notifications/cancelled when a request is abandoned. Neither needs
            // a response, and no other notification changes the read-only surface.
        }

        private JsonArray BuildToolList()
        {
            var tools = new JsonArray();

            foreach (var tool in _tools)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["inputSchema"] = tool.InputSchema.DeepClone()
                });
            }

            return tools;
        }

        private async Task HandleToolCallAsync(
            JsonElement id,
            JsonElement parameters,
            CancellationToken cancellationToken)
        {
            if (parameters.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                await SendErrorAsync(
                    id,
                    -32602,
                    "Invalid params for tools/call.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var name = nameElement.GetString() ?? string.Empty;
            var arguments = parameters.TryGetProperty("arguments", out var argumentsElement)
                ? argumentsElement
                : default;

            var tool = _tools.FirstOrDefault(
                candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));

            if (tool == null)
            {
                await SendErrorAsync(
                    id,
                    -32602,
                    $"Unknown tool: {name}",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            ToolResult result;

            try
            {
                result = await tool.Handler(arguments, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new ToolResult($"Error: {ex.Message}", true);
            }

            var content = new JsonArray();

            if (!string.IsNullOrEmpty(result.Text))
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = result.Text
                });
            }

            if (!string.IsNullOrEmpty(result.ImageBase64))
            {
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["data"] = result.ImageBase64,
                    ["mimeType"] = result.ImageMimeType ?? "image/png"
                });
            }

            await SendResultAsync(
                id,
                new JsonObject
                {
                    ["content"] = content,
                    ["isError"] = result.IsError
                },
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> EnsureInitializedAsync(
            JsonElement id,
            CancellationToken cancellationToken)
        {
            if (_initialized)
            {
                return true;
            }

            await SendErrorAsync(
                id,
                -32002,
                "Server is not initialized.",
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        private Task SendResultAsync(
            JsonElement id,
            JsonNode result,
            CancellationToken cancellationToken)
        {
            var payload = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["result"] = result
            };

            return _transport.WriteAsync(payload, cancellationToken);
        }

        private Task SendErrorAsync(
            JsonElement id,
            int code,
            string message,
            CancellationToken cancellationToken)
        {
            var payload = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["error"] = new JsonObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            };

            return _transport.WriteAsync(payload, cancellationToken);
        }
    }
}
