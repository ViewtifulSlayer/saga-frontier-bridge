using System;
using System.Threading.Tasks;

namespace SagaFrontierBridge.Mcp
{
    internal static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            try
            {
                var options = BridgeClientOptions.FromEnvironment();
                using var bridge = new BridgeClient(options);
                using var transport = new StdioTransport(
                    Console.OpenStandardInput(),
                    Console.OpenStandardOutput());
                var server = new McpServer(transport, bridge);

                return await server.RunAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"saga-frontier-bridge-mcp: {ex.Message}");
                return 1;
            }
        }
    }
}
