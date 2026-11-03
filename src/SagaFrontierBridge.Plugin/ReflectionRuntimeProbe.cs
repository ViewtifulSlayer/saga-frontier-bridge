using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using SagaFrontierBridge.Core;

namespace SagaFrontierBridge
{
    public sealed class ReflectionRuntimeProbe : IRuntimeProbe
    {
        private static readonly Dictionary<string, Type> TypeCache =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        public RuntimeSnapshot Capture()
        {
            try
            {
                var application = FindType("UnityEngine.Application");
                if (application == null)
                {
                    return new RuntimeSnapshot
                    {
                        Available = false,
                        Error = "UnityEngine.Application was not found in the loaded assemblies. " +
                                "The IL2CPP interop assemblies may not be generated yet.",
                        CapturedAtUtc = DateTimeOffset.UtcNow
                    };
                }

                var scene = ReadActiveScene();
                var time = FindType("UnityEngine.Time");

                return new RuntimeSnapshot
                {
                    Available = true,
                    UnityVersion = ReadStatic<string>(application, "unityVersion"),
                    GameVersion = ReadStatic<string>(application, "version"),
                    ProductName = ReadStatic<string>(application, "productName"),
                    CompanyName = ReadStatic<string>(application, "companyName"),
                    Scene = scene == null
                        ? null
                        : new RuntimeScene
                        {
                            Name = ReadInstance<string>(scene, "name"),
                            Path = ReadInstance<string>(scene, "path"),
                            BuildIndex = ReadInstance<int?>(scene, "buildIndex"),
                            IsLoaded = ReadInstance<bool?>(scene, "isLoaded")
                        },
                    Time = time == null
                        ? null
                        : new RuntimeTime
                        {
                            FrameCount = ReadStatic<long?>(time, "frameCount"),
                            RealtimeSinceStartup = ReadStatic<double?>(time, "realtimeSinceStartup"),
                            TimeScale = ReadStatic<double?>(time, "timeScale")
                        },
                    CapturedAtUtc = DateTimeOffset.UtcNow
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

        private static object? ReadActiveScene()
        {
            var sceneManager = FindType("UnityEngine.SceneManagement.SceneManager");
            if (sceneManager == null)
            {
                return null;
            }

            var getter = sceneManager
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, "GetActiveScene", StringComparison.Ordinal) &&
                    method.GetParameters().Length == 0);

            return getter?.Invoke(null, null);
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
                catch (Exception)
                {
                    // A missing or unloadable assembly must not stop the scan.
                }
            }

            return null;
        }

        private static T? ReadStatic<T>(Type type, string name)
        {
            try
            {
                var property = type.GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

                if (property != null)
                {
                    return ConvertValue<T>(property.GetValue(null));
                }

                var field = type.GetField(
                    name,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

                return field == null ? default : ConvertValue<T>(field.GetValue(null));
            }
            catch (Exception)
            {
                return default;
            }
        }

        private static T? ReadInstance<T>(object instance, string name)
        {
            try
            {
                var property = instance.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);

                return property == null
                    ? default
                    : ConvertValue<T>(property.GetValue(instance));
            }
            catch (Exception)
            {
                return default;
            }
        }

        private static T? ConvertValue<T>(object? value)
        {
            if (value == null)
            {
                return default;
            }

            var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

            if (targetType == typeof(string))
            {
                var text = Convert.ToString(value, CultureInfo.InvariantCulture);
                return (T)(object)(text ?? string.Empty);
            }

            if (targetType.IsInstanceOfType(value))
            {
                return (T)value;
            }

            var converted = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
            return (T)converted;
        }
    }
}
