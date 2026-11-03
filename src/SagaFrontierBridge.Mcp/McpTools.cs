using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SagaFrontierBridge.Mcp
{
    internal sealed record ToolResult(
        string Text,
        bool IsError = false,
        string? ImageBase64 = null,
        string? ImageMimeType = null);

    internal sealed record ToolDefinition(
        string Name,
        string Description,
        JsonObject InputSchema,
        Func<JsonElement, CancellationToken, Task<ToolResult>> Handler);

    internal static class McpTools
    {
        private static readonly JsonSerializerOptions PrettyJson = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private static readonly Regex SaveIdPattern = new Regex(
            "^[A-Za-z0-9_-]{1,64}$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static IReadOnlyList<ToolDefinition> Create(BridgeClient bridge)
        {
            if (bridge == null)
            {
                throw new ArgumentNullException(nameof(bridge));
            }

            return new[]
            {
                new ToolDefinition(
                    "bridge_status",
                    "Read-only snapshot of the running game bridge: health, Unity runtime state, and discovered saves. Use this first.",
                    EmptySchema(),
                    (arguments, cancellationToken) => StatusAsync(bridge, cancellationToken)),

                new ToolDefinition(
                    "bridge_runtime",
                    "Read-only Unity runtime state: engine and game versions, active scene, frame count, and time scale.",
                    EmptySchema(),
                    (arguments, cancellationToken) => GetAsync(
                        bridge,
                        "v1/runtime",
                        cancellationToken)),

                new ToolDefinition(
                    "bridge_save_work",
                    "Read-only summary of the live MainGame.SAVE_WORK object: test marker, party core, active party ids, roster count, and the SAVE_WORK checksum bytes. No writes or full arrays.",
                    EmptySchema(),
                    (arguments, cancellationToken) => GetAsync(
                        bridge,
                        "v1/save-work",
                        cancellationToken)),

                new ToolDefinition(
                    "bridge_party",
                    "Read-only live party detail: party core, active ids, roster ids, and per-member stats and equipment from SAVE_WORK.ply.",
                    EmptySchema(),
                    (arguments, cancellationToken) => GetAsync(
                        bridge,
                        "v1/party",
                        cancellationToken)),

                new ToolDefinition(
                    "bridge_flags",
                    "Read-only live flag arrays: 512-byte scenario flags, box flags, 1,024-byte scenario chart state, event flags/temp, event battle, and cfg1. Values are returned as integer arrays.",
                    EmptySchema(),
                    (arguments, cancellationToken) => GetAsync(
                        bridge,
                        "v1/flags",
                        cancellationToken)),

                new ToolDefinition(
                    "bridge_state",
                    "Read-only current room and map coordinates from SAVE_WORK: IMF room name, derived map id, area and region ids, X/Z position, direction, and previous map id.",
                    EmptySchema(),
                    (arguments, cancellationToken) => GetAsync(
                        bridge,
                        "v1/state",
                        cancellationToken)),

                new ToolDefinition(
                    "bridge_screenshot",
                    "Read-only live game screenshot as a PNG image. Capture is queued to the Unity main thread; the bridge keeps serving other requests while it is pending.",
                    EmptySchema(),
                    (arguments, cancellationToken) => ScreenshotAsync(bridge, cancellationToken)),

                new ToolDefinition(
                    "bridge_list_saves",
                    "List discovered SaveData.bin files with path, size, timestamp, and SHA-256. File contents are never returned.",
                    EmptySchema(),
                    (arguments, cancellationToken) => GetAsync(
                        bridge,
                        "v1/saves",
                        cancellationToken)),

                new ToolDefinition(
                    "bridge_get_save",
                    "Get metadata for one save id returned by bridge_list_saves. File contents are never returned.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["id"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Save id from bridge_list_saves."
                            }
                        },
                        ["required"] = new JsonArray { "id" },
                        ["additionalProperties"] = false
                    },
                    (arguments, cancellationToken) => GetSaveAsync(
                        bridge,
                        arguments,
                        cancellationToken)),

                new ToolDefinition(
                    "bridge_capabilities",
                    "Read-only bridge capabilities: accepted methods, implemented endpoints, and planned endpoints.",
                    EmptySchema(),
                    (arguments, cancellationToken) => GetAsync(
                        bridge,
                        "v1/capabilities",
                        cancellationToken))
            };
        }

        private static JsonObject EmptySchema()
        {
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = false
            };
        }

        private static async Task<ToolResult> StatusAsync(
            BridgeClient bridge,
            CancellationToken cancellationToken)
        {
            var health = await bridge.GetAsync("v1/health", cancellationToken)
                .ConfigureAwait(false);

            if (!health.IsSuccess)
            {
                return FromResponse(health, "v1/health");
            }

            var result = new JsonObject
            {
                ["health"] = ParseOrError(health)
            };

            var runtime = await bridge.GetAsync("v1/runtime", cancellationToken)
                .ConfigureAwait(false);
            result["runtime"] = runtime.IsSuccess
                ? ParseOrError(runtime)
                : ErrorNode(runtime);

            var saves = await bridge.GetAsync("v1/saves", cancellationToken)
                .ConfigureAwait(false);
            result["saves"] = saves.IsSuccess
                ? ParseOrError(saves)
                : ErrorNode(saves);

            return new ToolResult(result.ToJsonString(PrettyJson));
        }

        private static async Task<ToolResult> GetAsync(
            BridgeClient bridge,
            string path,
            CancellationToken cancellationToken)
        {
            var response = await bridge.GetAsync(path, cancellationToken)
                .ConfigureAwait(false);

            return FromResponse(response, path);
        }

        private static async Task<ToolResult> ScreenshotAsync(
            BridgeClient bridge,
            CancellationToken cancellationToken)
        {
            var response = await bridge.GetBytesAsync("v1/screenshot", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                var detail = response.Body.Length == 0
                    ? "(empty response body)"
                    : System.Text.Encoding.UTF8.GetString(response.Body);

                return new ToolResult(
                    $"Bridge returned HTTP {response.StatusCode} for /v1/screenshot: {detail}",
                    true);
            }

            if (response.Body.Length == 0)
            {
                return new ToolResult("The bridge returned an empty screenshot.", true);
            }

            response.Headers.TryGetValue("X-Screenshot-Width", out var width);
            response.Headers.TryGetValue("X-Screenshot-Height", out var height);
            var size = string.IsNullOrWhiteSpace(width) || string.IsNullOrWhiteSpace(height)
                ? "PNG"
                : $"{width}x{height} PNG";

            return new ToolResult(
                $"Live screenshot captured ({size}).",
                false,
                Convert.ToBase64String(response.Body),
                response.ContentType ?? "image/png");
        }

        private static async Task<ToolResult> GetSaveAsync(
            BridgeClient bridge,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            if (arguments.ValueKind != JsonValueKind.Object ||
                !arguments.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String)
            {
                return new ToolResult(
                    "Missing required string argument: id.",
                    true);
            }

            var id = idElement.GetString()?.Trim();
            if (string.IsNullOrEmpty(id) || !SaveIdPattern.IsMatch(id))
            {
                return new ToolResult(
                    "Invalid save id. Use an id returned by bridge_list_saves.",
                    true);
            }

            var path = $"v1/saves/{Uri.EscapeDataString(id)}";
            var response = await bridge.GetAsync(path, cancellationToken)
                .ConfigureAwait(false);

            return FromResponse(response, path);
        }

        private static ToolResult FromResponse(BridgeResponse response, string path)
        {
            if (response.IsSuccess)
            {
                return new ToolResult(response.Body);
            }

            var detail = string.IsNullOrWhiteSpace(response.Body)
                ? "(empty response body)"
                : response.Body;

            return new ToolResult(
                $"Bridge returned HTTP {response.StatusCode} for /{path}: {detail}",
                true);
        }

        private static JsonNode ErrorNode(BridgeResponse response)
        {
            return new JsonObject
            {
                ["error"] = $"HTTP {response.StatusCode}",
                ["body"] = string.IsNullOrWhiteSpace(response.Body)
                    ? "(empty response body)"
                    : response.Body
            };
        }

        private static JsonNode ParseOrError(BridgeResponse response)
        {
            try
            {
                return JsonNode.Parse(response.Body) ?? JsonValue.Create(response.Body)!;
            }
            catch (JsonException)
            {
                return new JsonObject
                {
                    ["raw"] = response.Body
                };
            }
        }
    }
}
