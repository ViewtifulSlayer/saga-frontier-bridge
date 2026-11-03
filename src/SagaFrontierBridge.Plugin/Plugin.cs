using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using SagaFrontierBridge.Core;

namespace SagaFrontierBridge
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BasePlugin
    {
        private BridgeServer? _server;
        private UnityScreenshotProbe? _screenshotProbe;

        public override void Load()
        {
            var enabled = Config.Bind(
                "Bridge",
                "Enabled",
                true,
                "Start the read-only loopback HTTP bridge.");

            var port = Config.Bind(
                "Bridge",
                "Port",
                BridgeOptions.DefaultPort,
                "Loopback port. The bridge always binds 127.0.0.1.");

            var accessToken = Config.Bind(
                "Bridge",
                "AccessToken",
                string.Empty,
                "Optional bearer token. Set a random value to require it on every request.");

            var saveRoot = Config.Bind(
                "Bridge",
                "SaveRoot",
                string.Empty,
                "Directory containing SaveData.bin files. Empty uses Documents\\My Games\\SaGa Frontier Remastered\\Steam.");

            if (!enabled.Value)
            {
                Log.LogInfo("SaGa Frontier Bridge is disabled.");
                return;
            }

            try
            {
                var options = new BridgeOptions
                {
                    Port = port.Value,
                    AccessToken = accessToken.Value,
                    SaveRoot = string.IsNullOrWhiteSpace(saveRoot.Value)
                        ? null
                        : saveRoot.Value
                };

                _screenshotProbe = new UnityScreenshotProbe(message => Log.LogInfo(message));

                _server = new BridgeServer(
                    options,
                    new ReflectionRuntimeProbe(),
                    new ReflectionSaveWorkProbe(),
                    _screenshotProbe,
                    message => Log.LogInfo(message));

                _server.Start();
                Log.LogInfo(
                    $"SaGa Frontier Bridge is listening at {_server.Url} (read-only).");

                if (string.IsNullOrWhiteSpace(accessToken.Value))
                {
                    Log.LogWarning(
                        "AccessToken is empty. Any local process can query the bridge.");
                }
            }
            catch (Exception ex)
            {
                _server?.Dispose();
                _server = null;
                Log.LogError($"SaGa Frontier Bridge failed to start: {ex}");
            }
        }

        public override bool Unload()
        {
            _server?.Dispose();
            _server = null;
            _screenshotProbe?.Detach();
            _screenshotProbe = null;
            return true;
        }
    }
}
