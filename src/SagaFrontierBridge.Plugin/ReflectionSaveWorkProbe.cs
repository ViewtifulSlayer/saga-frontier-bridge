using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using SagaFrontierBridge.Core;

namespace SagaFrontierBridge
{
    /// <summary>
    /// Read-only reflection probe for the live <c>MainGame.SAVE_WORK</c> object.
    /// It never invokes game logic, saves, or mutates the object graph.
    /// </summary>
    public sealed class ReflectionSaveWorkProbe : ISaveWorkProbe
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private const BindingFlags StaticFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
            BindingFlags.FlattenHierarchy;

        private static readonly string[] SaveFieldNames =
        {
            "SAVEWK_ADR",
            "save",
            "takeoverData"
        };

        private static readonly Dictionary<string, Type> TypeCache =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        public SaveWorkSnapshot Capture()
        {
            try
            {
                var context = OpenSaveWork(out var error);
                if (context == null)
                {
                    return Unavailable(error!);
                }

                var party = context.Party;
                var each = context.SaveLoaded
                    ? ReadInstanceMember(party!, "each")
                    : null;
                var sum = context.SaveLoaded
                    ? ReadByteArray(context.Work, "sum")
                    : null;

                return new SaveWorkSnapshot
                {
                    Available = true,
                    Source = context.Source,
                    SaveLoaded = context.SaveLoaded,
                    Test = ReadInt(context.Work, "test"),
                    IsTakeover = ReadBool(context.Work, "isTakeover"),
                    Party = !context.SaveLoaded || party == null
                        ? null
                        : BuildPartySummary(party, each),
                    PartyCharacterIds = context.SaveLoaded
                        ? ReadActivePartyIds(party)
                        : null,
                    RosterCount = context.SaveLoaded
                        ? ReadRosterIds(party)?.Length
                        : null,
                    SumLength = sum?.Length,
                    SumSha256 = sum == null ? null : Sha256(sum),
                    CapturedAtUtc = DateTimeOffset.UtcNow
                };
            }
            catch (Exception ex)
            {
                return Unavailable($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        public PartySnapshot CaptureParty()
        {
            try
            {
                var context = OpenSaveWork(out var error);
                if (context == null)
                {
                    return PartyUnavailable(error!);
                }

                if (!context.SaveLoaded || context.Party == null)
                {
                    return new PartySnapshot
                    {
                        Available = true,
                        Source = context.Source,
                        SaveLoaded = false,
                        CapturedAtUtc = DateTimeOffset.UtcNow
                    };
                }

                var party = context.Party;
                var active = ReadActivePartyIds(party);

                return new PartySnapshot
                {
                    Available = true,
                    Source = context.Source,
                    SaveLoaded = true,
                    Party = BuildPartySummary(
                        party,
                        ReadInstanceMember(party, "each")),
                    RosterSlotCount = ReadByteArray(party, "chrNo")?.Length,
                    ActiveCharacterIds = active,
                    RosterCharacterIds = ReadRosterIds(party),
                    Members = BuildMembers(context.Work, active),
                    CapturedAtUtc = DateTimeOffset.UtcNow
                };
            }
            catch (Exception ex)
            {
                return PartyUnavailable($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        public FlagSnapshot CaptureFlags()
        {
            try
            {
                var context = OpenSaveWork(out var error);
                if (context == null)
                {
                    return FlagUnavailable(error!);
                }

                if (!context.SaveLoaded || context.Party == null)
                {
                    return new FlagSnapshot
                    {
                        Available = true,
                        Source = context.Source,
                        SaveLoaded = false,
                        CapturedAtUtc = DateTimeOffset.UtcNow
                    };
                }

                var party = context.Party;
                var eventwk = ReadInstanceMember(context.Work, "eventwk");
                var scenarioFlags = ReadByteArray(party, "snroFlg");
                var chart = ReadByteArray(party, "scenarioChartStateArray");

                return new FlagSnapshot
                {
                    Available = true,
                    Source = context.Source,
                    SaveLoaded = true,
                    ScenarioFlags = ToIntArray(scenarioFlags),
                    BoxFlags = ToIntArray(ReadByteArray(party, "boxFlg")),
                    ScenarioChartState = ToIntArray(chart),
                    EventFlags = ToIntArray(ReadByteArray(eventwk, "wk")),
                    EventTemp = ToIntArray(ReadByteArray(eventwk, "temp")),
                    EventBattle = ReadUshortArray(party, "event_btl"),
                    Cfg1 = ToIntArray(ReadByteArray(party, "cfg1")),
                    SetScenarioFlagCount = CountNonZero(scenarioFlags),
                    SetScenarioChartCount = CountNonZero(chart),
                    CapturedAtUtc = DateTimeOffset.UtcNow
                };
            }
            catch (Exception ex)
            {
                return FlagUnavailable($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        public StateSnapshot CaptureState()
        {
            try
            {
                var context = OpenSaveWork(out var error);
                if (context == null)
                {
                    return StateUnavailable(error!);
                }

                if (!context.SaveLoaded)
                {
                    return new StateSnapshot
                    {
                        Available = true,
                        Source = context.Source,
                        SaveLoaded = false,
                        CapturedAtUtc = DateTimeOffset.UtcNow
                    };
                }

                var mapwk = ReadInstanceMember(context.Work, "mapwk");
                var eventwk = ReadInstanceMember(context.Work, "eventwk");
                var imfName = ReadString(mapwk, "imfName");

                return new StateSnapshot
                {
                    Available = true,
                    Source = context.Source,
                    SaveLoaded = true,
                    ImfName = imfName,
                    MapId = ParseImfMapId(imfName),
                    AreaId = ReadInt(mapwk, "map_name"),
                    RegionId = ReadInt(mapwk, "region_no"),
                    X = ReadInt(eventwk, "xpos"),
                    Z = ReadInt(eventwk, "zpos"),
                    Direction = ReadInt(eventwk, "dir"),
                    PreviousMapId = ReadInt(eventwk, "mapNo"),
                    CapturedAtUtc = DateTimeOffset.UtcNow
                };
            }
            catch (Exception ex)
            {
                return StateUnavailable($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static int? ParseImfMapId(string? imfName)
        {
            if (string.IsNullOrWhiteSpace(imfName))
            {
                return null;
            }

            var value = imfName!.Trim();
            if (!value.StartsWith("imf", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var hex = value.Substring(3).Trim();
            if (hex.Length == 0 || hex.Length > 4)
            {
                return null;
            }

            return int.TryParse(
                hex,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var id)
                ? id
                : (int?)null;
        }

        private static SaveWorkContext? OpenSaveWork(out string? error)
        {
            error = null;

            var mainGameType = FindType("MainGame");
            if (mainGameType == null)
            {
                error = "MainGame was not found in the loaded assemblies. " +
                    "The IL2CPP interop assemblies may not be generated yet.";
                return null;
            }

            var mainGame = ReadStaticMember(mainGameType, "Instance")
                ?? ReadStaticMember(mainGameType, "This");

            if (mainGame == null)
            {
                error = "MainGame.Instance is null. Load a save and try again.";
                return null;
            }

            var work = FindSaveWork(mainGame, out var source);
            if (work == null)
            {
                error = "No live SAVE_WORK field was found on MainGame. " +
                    "Expected one of: " + string.Join(", ", SaveFieldNames) + ".";
                return null;
            }

            return new SaveWorkContext(work, source);
        }

        private static SaveWorkParty BuildPartySummary(object party, object? each)
        {
            return new SaveWorkParty
            {
                PsnCnt = ReadInt(party, "psnCnt"),
                HeroNo = ReadInt(party, "heroNo"),
                Name = ReadString(party, "name"),
                Name2 = ReadString(party, "name2"),
                Money = ReadUInt(party, "money"),
                ParLev = each == null ? null : ReadInt(each, "parLev"),
                PlayTimeHours = ReadInt(party, "PlayTimeHours"),
                PlayTimeMinutes = ReadInt(party, "PlayTimeMinutes"),
                PlayTimeSeconds = ReadDouble(party, "PlayTimeSeconds")
            };
        }

        private static PartyMemberSnapshot[]? BuildMembers(object work, int[]? active)
        {
            if (active == null)
            {
                return null;
            }

            var ply = ReadInstanceMember(work, "ply") as IEnumerable;
            if (ply == null)
            {
                return null;
            }

            var records = new Dictionary<int, object>();
            foreach (var record in ply)
            {
                if (record == null)
                {
                    continue;
                }

                var id = ReadInt(record, "chrNo");
                if (id.HasValue && !records.ContainsKey(id.Value))
                {
                    records[id.Value] = record;
                }
            }

            var members = new List<PartyMemberSnapshot>(active.Length);
            foreach (var id in active)
            {
                records.TryGetValue(id, out var record);
                members.Add(BuildMember(id, record));
            }

            return members.ToArray();
        }

        private static PartyMemberSnapshot BuildMember(int id, object? record)
        {
            return new PartyMemberSnapshot
            {
                CharacterId = id,
                HpMax = ReadInt(record, "hpMax"),
                LpNow = ReadInt(record, "lpNow"),
                LpMax = ReadInt(record, "lpMax"),
                WpNow = ReadInt(record, "wpNow"),
                WpMax = ReadInt(record, "wpMax"),
                JpNow = ReadInt(record, "jpNow"),
                JpMax = ReadInt(record, "jpMax"),
                StrNow = ReadInt(record, "strNow"),
                DexNow = ReadInt(record, "dexNow"),
                HndNow = ReadInt(record, "hndNow"),
                MndNow = ReadInt(record, "mndNow"),
                MgcNow = ReadInt(record, "mgcNow"),
                ConNow = ReadInt(record, "conNow"),
                ChmNow = ReadInt(record, "chmNow"),
                Race = ReadInt(record, "race"),
                Nature = ReadInt(record, "nature"),
                CharacterType = ReadInt(record, "getType"),
                AllLpLoss = ReadInt(record, "allLpLoss"),
                BattleCount = ReadInt(record, "btlCnt"),
                EquipmentItemIds = ToIntArray(ReadByteArray(record, "eqpItm")),
                EquipmentAttackIds = ReadUshortArray(record, "eqpAtk")
            };
        }

        private static object? FindSaveWork(object mainGame, out string source)
        {
            foreach (var name in SaveFieldNames)
            {
                var value = ReadInstanceMember(mainGame, name);
                if (value == null)
                {
                    continue;
                }

                var typeName = value.GetType().Name;
                if (typeName == "SAVE_WORK" ||
                    typeName.EndsWith(".SAVE_WORK", StringComparison.Ordinal))
                {
                    source = name;
                    return value;
                }
            }

            source = string.Empty;
            return null;
        }

        private static int[]? ReadActivePartyIds(object? party)
        {
            return FilterIds(ReadByteArray(party, "team"));
        }

        private static int[]? ReadRosterIds(object? party)
        {
            return FilterIds(ReadByteArray(party, "chrNo"));
        }

        private static int[]? FilterIds(byte[]? ids)
        {
            if (ids == null)
            {
                return null;
            }

            var active = new List<int>();
            foreach (var id in ids)
            {
                if (id != 255)
                {
                    active.Add(id);
                }
            }

            return active.ToArray();
        }

        private static int[]? ToIntArray(byte[]? bytes)
        {
            return bytes?.Select(value => (int)value).ToArray();
        }

        private static int[]? ReadUshortArray(object? instance, string name)
        {
            var value = instance == null ? null : ReadInstanceMember(instance, name);
            if (value == null)
            {
                return null;
            }

            if (value is ushort[] ushorts)
            {
                return ushorts.Select(item => (int)item).ToArray();
            }

            if (value is IEnumerable enumerable)
            {
                var result = new List<int>();
                foreach (var item in enumerable)
                {
                    if (item == null)
                    {
                        break;
                    }

                    result.Add(Convert.ToInt32(item, CultureInfo.InvariantCulture));
                }

                return result.ToArray();
            }

            return null;
        }

        private static byte[]? ReadByteArray(object? instance, string name)
        {
            var value = instance == null ? null : ReadInstanceMember(instance, name);
            if (value == null)
            {
                return null;
            }

            if (value is byte[] bytes)
            {
                return bytes;
            }

            if (value is IEnumerable enumerable)
            {
                var result = new List<byte>();
                foreach (var item in enumerable)
                {
                    if (item == null)
                    {
                        break;
                    }

                    result.Add(Convert.ToByte(item, CultureInfo.InvariantCulture));
                }

                return result.ToArray();
            }

            return null;
        }

        private static int? CountNonZero(byte[]? bytes)
        {
            if (bytes == null)
            {
                return null;
            }

            var count = 0;
            foreach (var value in bytes)
            {
                if (value != 0)
                {
                    count++;
                }
            }

            return count;
        }

        private static int? ReadInt(object? instance, string name)
        {
            var value = instance == null ? null : ReadInstanceMember(instance, name);
            return value == null
                ? null
                : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static uint? ReadUInt(object? instance, string name)
        {
            var value = instance == null ? null : ReadInstanceMember(instance, name);
            return value == null
                ? null
                : Convert.ToUInt32(value, CultureInfo.InvariantCulture);
        }

        private static double? ReadDouble(object? instance, string name)
        {
            var value = instance == null ? null : ReadInstanceMember(instance, name);
            return value == null
                ? null
                : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        private static bool? ReadBool(object? instance, string name)
        {
            var value = instance == null ? null : ReadInstanceMember(instance, name);
            return value == null
                ? null
                : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        private static string? ReadString(object? instance, string name)
        {
            return instance == null ? null : ReadInstanceMember(instance, name) as string;
        }

        private static object? ReadInstanceMember(object instance, string name)
        {
            var type = instance.GetType();

            var field = type.GetField(name, InstanceFlags);
            if (field != null)
            {
                return field.GetValue(instance);
            }

            var property = type.GetProperty(name, InstanceFlags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(instance);
            }

            return null;
        }

        private static object? ReadStaticMember(Type type, string name)
        {
            var property = type.GetProperty(name, StaticFlags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(null);
            }

            var field = type.GetField(name, StaticFlags);
            return field?.GetValue(null);
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
        }

        private static SaveWorkSnapshot Unavailable(string error)
        {
            return new SaveWorkSnapshot
            {
                Available = false,
                Error = error,
                CapturedAtUtc = DateTimeOffset.UtcNow
            };
        }

        private static PartySnapshot PartyUnavailable(string error)
        {
            return new PartySnapshot
            {
                Available = false,
                Error = error,
                CapturedAtUtc = DateTimeOffset.UtcNow
            };
        }

        private static FlagSnapshot FlagUnavailable(string error)
        {
            return new FlagSnapshot
            {
                Available = false,
                Error = error,
                CapturedAtUtc = DateTimeOffset.UtcNow
            };
        }

        private static StateSnapshot StateUnavailable(string error)
        {
            return new StateSnapshot
            {
                Available = false,
                Error = error,
                CapturedAtUtc = DateTimeOffset.UtcNow
            };
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

        private sealed class SaveWorkContext
        {
            public SaveWorkContext(object work, string source)
            {
                Work = work;
                Source = source;
                Party = ReadInstanceMember(work, "pty");
                var psnCnt = ReadInt(Party, "psnCnt");
                SaveLoaded = psnCnt.HasValue && psnCnt.Value > 0;
            }

            public object Work { get; }

            public object? Party { get; }

            public string Source { get; }

            public bool SaveLoaded { get; }
        }
    }
}
