using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SagaFrontierBridge.Mcp
{
    internal sealed class StdioTransport : IDisposable
    {
        private enum Framing
        {
            ContentLength,
            NewlineJson
        }

        private const int MaxHeaderLineBytes = 64 * 1024;
        private const int MaxBodyBytes = 16 * 1024 * 1024;

        private readonly Stream _input;
        private readonly Stream _output;
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private Framing _framing = Framing.ContentLength;

        public StdioTransport(Stream input, Stream output)
        {
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        public async Task<JsonDocument?> ReadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var firstLine = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (firstLine == null)
                {
                    return null;
                }

                if (firstLine.Length == 0)
                {
                    continue;
                }

                if (firstLine.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    _framing = Framing.NewlineJson;
                    return JsonDocument.Parse(firstLine);
                }

                int? contentLength = TryParseContentLength(firstLine);

                while (true)
                {
                    var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line == null)
                    {
                        return null;
                    }

                    if (line.Length == 0)
                    {
                        break;
                    }

                    var parsed = TryParseContentLength(line);
                    if (parsed.HasValue)
                    {
                        contentLength = parsed;
                    }
                }

                if (contentLength == null || contentLength <= 0)
                {
                    throw new InvalidDataException(
                        "The MCP frame did not contain a valid Content-Length header.");
                }

                if (contentLength > MaxBodyBytes)
                {
                    throw new InvalidDataException(
                        "The MCP frame exceeded the maximum accepted size.");
                }

                var payload = await ReadExactAsync(contentLength.Value, cancellationToken)
                    .ConfigureAwait(false);
                _framing = Framing.ContentLength;
                return JsonDocument.Parse(payload);
            }
        }

        public async Task WriteAsync(JsonNode payload, CancellationToken cancellationToken)
        {
            var json = payload.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = false
            });

            var bytes = Encoding.UTF8.GetBytes(json);

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_framing == Framing.NewlineJson)
                {
                    await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await _output.WriteAsync(
                        new[] { (byte)'\n' },
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var header = Encoding.ASCII.GetBytes(
                        $"Content-Length: {bytes.Length}\r\n\r\n");
                    await _output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                    await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                }

                await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public void Dispose()
        {
            _writeGate.Dispose();
        }

        private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            var buffer = new byte[1];

            while (true)
            {
                var read = await _input.ReadAsync(
                    buffer.AsMemory(0, 1),
                    cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    return builder.Length == 0 ? null : builder.ToString();
                }

                var value = buffer[0];
                if (value == (byte)'\n')
                {
                    return builder.ToString().TrimEnd('\r');
                }

                if (builder.Length >= MaxHeaderLineBytes)
                {
                    throw new InvalidDataException(
                        "The MCP header line exceeded the maximum accepted size.");
                }

                builder.Append((char)value);
            }
        }

        private async Task<byte[]> ReadExactAsync(
            int length,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[length];
            var offset = 0;

            while (offset < length)
            {
                var read = await _input.ReadAsync(
                    buffer.AsMemory(offset, length - offset),
                    cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "The MCP frame ended before its declared Content-Length.");
                }

                offset += read;
            }

            return buffer;
        }

        private static int? TryParseContentLength(string line)
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                return null;
            }

            var key = line.Substring(0, separator).Trim();
            if (!key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var value = line.Substring(separator + 1).Trim();
            return int.TryParse(value, out var length) ? length : null;
        }
    }
}
