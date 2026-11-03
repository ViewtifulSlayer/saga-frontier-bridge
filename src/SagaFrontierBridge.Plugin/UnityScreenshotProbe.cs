using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using SagaFrontierBridge.Core;

namespace SagaFrontierBridge
{
    /// <summary>
    /// Queues screenshot work to the Unity main thread. The HTTP worker awaits
    /// the task; it never sleeps or pumps the Unity loop itself. Capture is
    /// driven by a Harmony postfix on Application.InvokeOnBeforeRender, which
    /// runs each frame on the main thread.
    /// </summary>
    public sealed class UnityScreenshotProbe : IScreenshotProbe
    {
        private static readonly BindingFlags StaticFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static readonly Dictionary<string, Type> TypeCache =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        private static UnityScreenshotProbe? _instance;

        private readonly object _gate = new object();
        private readonly Queue<CaptureRequest> _pending = new Queue<CaptureRequest>();
        private CaptureRequest? _inFlight;
        private readonly SynchronizationContext? _context;
        private readonly bool _useContext;
        private readonly Action<string>? _log;
        private Harmony? _harmony;
        private volatile bool _frameHook;

        public UnityScreenshotProbe(Action<string>? log = null)
        {
            _log = log;
            _context = SynchronizationContext.Current;
            _useContext = _context != null &&
                (_context.GetType().FullName?.IndexOf(
                    "Unity",
                    StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;

            _instance = this;
            _frameHook = TryInstallFrameHook();

            Log(
                $"Screenshot capture: context={_context?.GetType().FullName ?? "null"}, " +
                $"useContext={_useContext}, frameHook={_frameHook}.");
        }

        public Task<ScreenshotSnapshot> CaptureAsync(CancellationToken cancellationToken)
        {
            EnsureDispatchPath();

            if (!_useContext && !_frameHook)
            {
                return Task.FromResult(Unavailable(
                    "No Unity main-thread dispatch path is available for screenshots."));
            }

            var request = new CaptureRequest(cancellationToken);
            lock (_gate)
            {
                _pending.Enqueue(request);
            }

            if (!_frameHook && _useContext)
            {
                try
                {
                    _context!.Post(_ => Drain(), null);
                }
                catch (Exception ex)
                {
                    Log($"Screenshot context post failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return request.Task;
        }

        /// <summary>
        /// Removes the static Unity callback. Safe to call more than once.
        /// </summary>
        public void Detach()
        {
            var harmony = _harmony;
            _harmony = null;
            _frameHook = false;
            _instance = null;

            var inFlight = _inFlight;
            _inFlight = null;
            if (inFlight != null)
            {
                Destroy(inFlight.Texture);
                inFlight.Texture = null;
                inFlight.TrySetCanceled();
            }

            if (harmony == null)
            {
                return;
            }

            try
            {
                harmony.UnpatchSelf();
            }
            catch (Exception ex)
            {
                Log($"Screenshot frame hook removal failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void EnsureDispatchPath()
        {
            if (_useContext || _frameHook)
            {
                return;
            }

            lock (_gate)
            {
                if (_frameHook)
                {
                    return;
                }

                _frameHook = TryInstallFrameHook();
                Log($"Screenshot capture retry: frameHook={_frameHook}.");
            }
        }

        private void Drain()
        {
            // CaptureScreenshotAsTexture fills the texture at the end of the
            // frame, so capture on one tick and encode on the next.
            FinishInFlight();
            StartNextCapture();
        }

        private void FinishInFlight()
        {
            var request = _inFlight;
            _inFlight = null;

            if (request == null)
            {
                return;
            }

            try
            {
                if (request.CancellationToken.IsCancellationRequested)
                {
                    request.TrySetCanceled();
                    return;
                }

                request.TrySetResult(EncodeCaptured(request));
            }
            catch (Exception ex)
            {
                request.TrySetResult(Unavailable($"{ex.GetType().Name}: {ex.Message}"));
            }
            finally
            {
                Destroy(request.Texture);
                request.Texture = null;
            }
        }

        private void StartNextCapture()
        {
            while (true)
            {
                CaptureRequest request;

                lock (_gate)
                {
                    if (_pending.Count == 0)
                    {
                        return;
                    }

                    request = _pending.Dequeue();
                }

                if (request.CancellationToken.IsCancellationRequested)
                {
                    request.TrySetCanceled();
                    continue;
                }

                var texture = CaptureTexture(out var width, out var height, out var error);
                if (texture == null)
                {
                    request.TrySetResult(Unavailable(error ?? "ScreenCapture returned no texture."));
                    continue;
                }

                request.Texture = texture;
                request.Width = width;
                request.Height = height;
                _inFlight = request;
                return;
            }
        }

        private static void OnBeforeRender()
        {
            _instance?.Drain();
        }

        private static object? CaptureTexture(
            out int? width,
            out int? height,
            out string? error)
        {
            width = null;
            height = null;
            error = null;

            var screenCapture = FindType("UnityEngine.ScreenCapture");
            var capture = screenCapture?
                .GetMethods(StaticFlags)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, "CaptureScreenshotAsTexture", StringComparison.Ordinal) &&
                    method.GetParameters().Length == 0);

            if (capture == null)
            {
                error =
                    "UnityEngine.ScreenCapture.CaptureScreenshotAsTexture was not found.";
                return null;
            }

            var texture = capture.Invoke(null, null);
            if (texture == null)
            {
                error = "ScreenCapture returned no texture.";
                return null;
            }

            width = ReadIntProperty(texture, "width");
            height = ReadIntProperty(texture, "height");
            return texture;
        }

        private static ScreenshotSnapshot EncodeCaptured(CaptureRequest request)
        {
            var texture = request.Texture;
            if (texture == null)
            {
                return Unavailable("The captured texture was lost before encoding.");
            }

            var encode = FindEncodeToPng(texture.GetType());
            if (encode == null)
            {
                return Unavailable("EncodeToPNG was not found in the loaded Unity assemblies.");
            }

            var encoded = encode.IsStatic
                ? encode.Invoke(null, new[] { texture })
                : encode.Invoke(texture, null);
            var png = ToByteArray(encoded);

            if (png == null || png.Length == 0)
            {
                return Unavailable("EncodeToPNG returned no PNG bytes.");
            }

            return new ScreenshotSnapshot
            {
                Available = true,
                Png = png,
                Width = request.Width,
                Height = request.Height,
                CapturedAtUtc = DateTimeOffset.UtcNow
            };
        }

        private static byte[]? ToByteArray(object? value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is byte[] bytes)
            {
                return bytes;
            }

            // EncodeToPNG returns Il2CppStructArray<byte>, not a managed array.
            if (value is IEnumerable<byte> sequence)
            {
                return sequence.ToArray();
            }

            var toArray = value.GetType().GetMethod(
                "ToArray",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            return toArray?.Invoke(value, null) as byte[];
        }

        private static MethodInfo? FindEncodeToPng(Type textureType)
        {
            var imageConversion = FindType("UnityEngine.ImageConversion");
            var staticMethod = imageConversion?
                .GetMethods(StaticFlags)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, "EncodeToPNG", StringComparison.Ordinal) &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType.IsAssignableFrom(textureType));

            if (staticMethod != null)
            {
                return staticMethod;
            }

            return textureType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, "EncodeToPNG", StringComparison.Ordinal) &&
                    method.GetParameters().Length == 0);
        }

        private static void Destroy(object? texture)
        {
            if (texture == null)
            {
                return;
            }

            try
            {
                var objectType = FindType("UnityEngine.Object");
                var destroy = objectType?
                    .GetMethods(StaticFlags)
                    .FirstOrDefault(method =>
                        string.Equals(method.Name, "Destroy", StringComparison.Ordinal) &&
                        method.GetParameters().Length == 1 &&
                        method.GetParameters()[0].ParameterType.IsInstanceOfType(texture));

                destroy?.Invoke(null, new[] { texture });
            }
            catch
            {
                // Releasing a capture texture is best-effort.
            }
        }

        private static int? ReadIntProperty(object instance, string name)
        {
            try
            {
                var property = instance.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);
                return property == null
                    ? null
                    : Convert.ToInt32(property.GetValue(instance), CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private bool TryInstallFrameHook()
        {
            try
            {
                var application = FindType("UnityEngine.Application");
                var target = application?.GetMethod(
                    "InvokeOnBeforeRender",
                    StaticFlags,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);

                if (target == null)
                {
                    Log(
                        "Screenshot frame hook: UnityEngine.Application.InvokeOnBeforeRender was not found.");
                    return false;
                }

                var postfix = typeof(UnityScreenshotProbe).GetMethod(
                    nameof(OnBeforeRender),
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (postfix == null)
                {
                    return false;
                }

                var harmony = new Harmony("com.viewtifulslayer.sagafrontier.bridge.screenshot");
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));

                _harmony = harmony;
                return true;
            }
            catch (Exception ex)
            {
                Log($"Screenshot frame hook failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static ScreenshotSnapshot Unavailable(string error)
        {
            return new ScreenshotSnapshot
            {
                Available = false,
                Error = error,
                CapturedAtUtc = DateTimeOffset.UtcNow
            };
        }

        private void Log(string message)
        {
            try
            {
                _log?.Invoke(message);
            }
            catch
            {
            }
        }

        private static Type? FindType(string fullName)
        {
            lock (TypeCache)
            {
                if (TypeCache.TryGetValue(fullName, out var cached))
                {
                    return cached;
                }
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                {
                    continue;
                }

                try
                {
                    var type = assembly.GetType(fullName, throwOnError: false);
                    if (type == null)
                    {
                        continue;
                    }

                    lock (TypeCache)
                    {
                        TypeCache[fullName] = type;
                    }

                    return type;
                }
                catch
                {
                    // A missing assembly must not stop the scan.
                }
            }

            return null;
        }

        private sealed class CaptureRequest
        {
            private readonly TaskCompletionSource<ScreenshotSnapshot> _source =
                new TaskCompletionSource<ScreenshotSnapshot>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            public CaptureRequest(CancellationToken cancellationToken)
            {
                CancellationToken = cancellationToken;
            }

            public CancellationToken CancellationToken { get; }

            public object? Texture { get; set; }

            public int? Width { get; set; }

            public int? Height { get; set; }

            public Task<ScreenshotSnapshot> Task => _source.Task;

            public void TrySetResult(ScreenshotSnapshot result)
            {
                _source.TrySetResult(result);
            }

            public void TrySetCanceled()
            {
                _source.TrySetCanceled();
            }
        }
    }
}
