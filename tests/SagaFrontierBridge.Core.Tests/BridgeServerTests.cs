using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SagaFrontierBridge.Core;
using Xunit;

namespace SagaFrontierBridge.Core.Tests
{
    public sealed class BridgeServerTests : IDisposable
    {
        private const string Token = "test-token";
        private readonly string _saveRoot;
        private readonly int _port;
        private readonly BridgeServer _server;
        private readonly HttpClient _client;

        public BridgeServerTests()
        {
            _saveRoot = Path.Combine(
                Path.GetTempPath(),
                "saga-frontier-bridge-tests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(_saveRoot);
            File.WriteAllBytes(
                Path.Combine(_saveRoot, "SaveData.bin"),
                new byte[] { 0x78, 0x9C, 0x01, 0x02, 0x03 });

            _port = GetFreePort();
            _server = new BridgeServer(
                new BridgeOptions
                {
                    Port = _port,
                    AccessToken = Token,
                    SaveRoot = _saveRoot,
                    RequestTimeout = TimeSpan.FromSeconds(2)
                },
                new UnavailableRuntimeProbe("Runtime state is not available in unit tests."),
                new StubSaveWorkProbe(),
                new StubScreenshotProbe());

            _server.Start();

            _client = CreateClient();
            _client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", Token);
        }

        [Fact]
        public async Task Health_ReportsReadOnlyAndSaveCount()
        {
            using var response = await _client.GetAsync("v1/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());

            Assert.True(document.RootElement.GetProperty("readOnly").GetBoolean());
            Assert.Equal(1, document.RootElement.GetProperty("saveCount").GetInt32());
            Assert.False(document.RootElement.GetProperty("runtimeAvailable").GetBoolean());
        }

        [Fact]
        public async Task Runtime_ReportsUnavailableProbeWithoutFailing()
        {
            using var response = await _client.GetAsync("v1/runtime");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());

            Assert.False(document.RootElement.GetProperty("available").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(
                document.RootElement.GetProperty("error").GetString()));
        }

        [Fact]
        public async Task SaveWork_ExposesLiveSummary()
        {
            using var response = await _client.GetAsync("v1/save-work");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());

            var root = document.RootElement;
            Assert.True(root.GetProperty("available").GetBoolean());
            Assert.True(root.GetProperty("saveLoaded").GetBoolean());
            Assert.Equal("SAVEWK_ADR", root.GetProperty("source").GetString());
            Assert.Equal(75, root.GetProperty("test").GetInt32());
            Assert.False(root.GetProperty("isTakeover").GetBoolean());

            var party = root.GetProperty("party");
            Assert.Equal(1, party.GetProperty("psnCnt").GetInt32());
            Assert.Equal(5, party.GetProperty("heroNo").GetInt32());
            Assert.Equal("Asellus", party.GetProperty("name").GetString());
            Assert.Equal(100u, party.GetProperty("money").GetUInt32());
            Assert.Equal(23, party.GetProperty("parLev").GetInt32());

            Assert.Equal(1, root.GetProperty("partyCharacterIds").GetArrayLength());
            Assert.Equal(1, root.GetProperty("rosterCount").GetInt32());
            Assert.Equal(4, root.GetProperty("sumLength").GetInt32());
            Assert.Equal(
                "9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a",
                root.GetProperty("sumSha256").GetString());
        }

        [Fact]
        public async Task Party_ExposesMembersAndRoster()
        {
            using var response = await _client.GetAsync("v1/party");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());

            var root = document.RootElement;
            Assert.True(root.GetProperty("available").GetBoolean());
            Assert.True(root.GetProperty("saveLoaded").GetBoolean());
            Assert.Equal("SAVEWK_ADR", root.GetProperty("source").GetString());
            Assert.Equal(56, root.GetProperty("rosterSlotCount").GetInt32());
            Assert.Equal(2, root.GetProperty("activeCharacterIds").GetArrayLength());
            Assert.Equal(3, root.GetProperty("rosterCharacterIds").GetArrayLength());

            var members = root.GetProperty("members");
            Assert.Equal(2, members.GetArrayLength());
            Assert.Equal(5, members[0].GetProperty("characterId").GetInt32());
            Assert.Equal(320, members[0].GetProperty("hpMax").GetInt32());
            Assert.Equal(1, members[0].GetProperty("nature").GetInt32());
            Assert.Equal(
                2,
                members[0].GetProperty("equipmentItemIds").GetArrayLength());
            Assert.Equal(
                2,
                members[0].GetProperty("equipmentAttackIds").GetArrayLength());
        }

        [Fact]
        public async Task Flags_ExposeIntegerArrays()
        {
            using var response = await _client.GetAsync("v1/flags");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());

            var root = document.RootElement;
            Assert.True(root.GetProperty("available").GetBoolean());
            Assert.True(root.GetProperty("saveLoaded").GetBoolean());
            Assert.Equal(3, root.GetProperty("scenarioFlags").GetArrayLength());
            Assert.Equal(240, root.GetProperty("scenarioFlags")[0].GetInt32());
            Assert.Equal(2, root.GetProperty("scenarioChartState")[157].GetInt32());
            Assert.Equal(2, root.GetProperty("cfg1").GetArrayLength());
            Assert.Equal(1, root.GetProperty("setScenarioFlagCount").GetInt32());
            Assert.Equal(2, root.GetProperty("setScenarioChartCount").GetInt32());
        }

        [Fact]
        public async Task State_ExposesCurrentRoomAndCoordinates()
        {
            using var response = await _client.GetAsync("v1/state");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());

            var root = document.RootElement;
            Assert.True(root.GetProperty("available").GetBoolean());
            Assert.True(root.GetProperty("saveLoaded").GetBoolean());
            Assert.Equal("SAVEWK_ADR", root.GetProperty("source").GetString());
            Assert.Equal("imf1b0", root.GetProperty("imfName").GetString());
            Assert.Equal(432, root.GetProperty("mapId").GetInt32());
            Assert.Equal(26, root.GetProperty("areaId").GetInt32());
            Assert.Equal(17, root.GetProperty("regionId").GetInt32());
            Assert.Equal(32, root.GetProperty("x").GetInt32());
            Assert.Equal(5120, root.GetProperty("z").GetInt32());
            Assert.Equal(17, root.GetProperty("direction").GetInt32());
            Assert.Equal(775, root.GetProperty("previousMapId").GetInt32());
        }

        [Fact]
        public async Task Screenshot_ReturnsPngWithDimensions()
        {
            using var response = await _client.GetAsync("v1/screenshot");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("1", response.Headers.GetValues("X-Screenshot-Width").Single());
            Assert.Equal("1", response.Headers.GetValues("X-Screenshot-Height").Single());

            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(StubScreenshotProbe.Png, bytes);
        }

        [Fact]
        public async Task Screenshot_DoesNotBlockOtherRequestsWhilePending()
        {
            var probe = new BlockingScreenshotProbe();
            var port = GetFreePort();

            using var server = new BridgeServer(
                new BridgeOptions
                {
                    Port = port,
                    AccessToken = Token,
                    SaveRoot = _saveRoot,
                    RequestTimeout = TimeSpan.FromSeconds(5)
                },
                new UnavailableRuntimeProbe("Runtime state is not available in unit tests."),
                new StubSaveWorkProbe(),
                probe);
            server.Start();

            using var client = CreateClient(port);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", Token);

            var screenshotTask = client.GetAsync("v1/screenshot");
            await probe.Started.WaitAsync(TimeSpan.FromSeconds(2));

            using var health = await client.GetAsync("v1/health")
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            probe.Release();

            using var screenshot = await screenshotTask
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(HttpStatusCode.OK, screenshot.StatusCode);
            Assert.Equal("image/png", screenshot.Content.Headers.ContentType!.MediaType);
        }

        [Fact]
        public async Task ScreenshotHead_ReturnsHeadersWithoutABody()
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, "v1/screenshot");
            using var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }

        [Fact]
        public async Task Capabilities_ListProbeEndpoints()
        {
            using var response = await _client.GetAsync("v1/capabilities");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());

            var endpoints = new System.Collections.Generic.List<string>();
            foreach (var endpoint in document.RootElement.GetProperty("endpoints").EnumerateArray())
            {
                endpoints.Add(endpoint.GetString()!);
            }

            Assert.Contains("/v1/save-work", endpoints);
            Assert.Contains("/v1/party", endpoints);
            Assert.Contains("/v1/flags", endpoints);
            Assert.Contains("/v1/state", endpoints);
            Assert.Contains("/v1/screenshot", endpoints);
        }

        [Fact]
        public async Task Saves_ListsMetadataAndFetchingOneByIdWorks()
        {
            using var listResponse = await _client.GetAsync("v1/saves");
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

            using var listDocument = JsonDocument.Parse(
                await listResponse.Content.ReadAsStringAsync());

            var saves = listDocument.RootElement.GetProperty("saves");
            Assert.Equal(1, saves.GetArrayLength());

            var save = saves[0];
            var id = save.GetProperty("id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.Equal(64, save.GetProperty("sha256").GetString()!.Length);

            using var itemResponse = await _client.GetAsync($"v1/saves/{id}");
            Assert.Equal(HttpStatusCode.OK, itemResponse.StatusCode);

            using var itemDocument = JsonDocument.Parse(
                await itemResponse.Content.ReadAsStringAsync());

            Assert.Equal(id, itemDocument.RootElement.GetProperty("id").GetString());
        }

        [Fact]
        public async Task Post_IsRejected()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/health");
            using var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        [Fact]
        public async Task MissingToken_IsRejected()
        {
            using var anonymous = CreateClient();
            using var response = await anonymous.GetAsync("v1/health");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task ForeignHostHeader_IsRejected()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/health");
            request.Headers.Host = "evil.example";

            using var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Head_ReturnsHeadersWithoutABody()
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, "v1/health");
            using var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }

        [Fact]
        public async Task UnknownAndContentRoutes_AreNotExposed()
        {
            using var unknown = await _client.GetAsync("v1/not-a-route");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

            using var contents = await _client.GetAsync("v1/saves/not-a-real-id/bytes");
            Assert.Equal(HttpStatusCode.NotFound, contents.StatusCode);
        }

        public void Dispose()
        {
            _client.Dispose();
            _server.Dispose();

            try
            {
                Directory.Delete(_saveRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private HttpClient CreateClient()
        {
            return CreateClient(_port);
        }

        private static HttpClient CreateClient(int port)
        {
            var handler = new HttpClientHandler
            {
                UseProxy = false
            };

            return new HttpClient(handler)
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromSeconds(5)
            };
        }

        private sealed class StubSaveWorkProbe : ISaveWorkProbe
        {
            public SaveWorkSnapshot Capture()
            {
                return new SaveWorkSnapshot
                {
                    Available = true,
                    Source = "SAVEWK_ADR",
                    SaveLoaded = true,
                    Test = 75,
                    IsTakeover = false,
                    Party = new SaveWorkParty
                    {
                        PsnCnt = 1,
                        HeroNo = 5,
                        Name = "Asellus",
                        Name2 = "Asellus",
                        Money = 100,
                        ParLev = 23,
                        PlayTimeHours = 0,
                        PlayTimeMinutes = 11,
                        PlayTimeSeconds = 42f
                    },
                    PartyCharacterIds = new[] { 5 },
                    RosterCount = 1,
                    SumLength = 4,
                    SumSha256 =
                        "9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a"
                };
            }

            public PartySnapshot CaptureParty()
            {
                return new PartySnapshot
                {
                    Available = true,
                    Source = "SAVEWK_ADR",
                    SaveLoaded = true,
                    Party = new SaveWorkParty
                    {
                        PsnCnt = 2,
                        HeroNo = 5,
                        Name = "Asellus",
                        Money = 100,
                        ParLev = 23
                    },
                    RosterSlotCount = 56,
                    ActiveCharacterIds = new[] { 5, 7 },
                    RosterCharacterIds = new[] { 5, 7, 9 },
                    Members = new[]
                    {
                        new PartyMemberSnapshot
                        {
                            CharacterId = 5,
                            HpMax = 320,
                            LpNow = 10,
                            LpMax = 12,
                            Nature = 1,
                            Race = 1,
                            EquipmentItemIds = new[] { 1, 2 },
                            EquipmentAttackIds = new[] { 3, 4 }
                        },
                        new PartyMemberSnapshot
                        {
                            CharacterId = 7,
                            HpMax = 280,
                            Nature = 0
                        }
                    }
                };
            }

            public FlagSnapshot CaptureFlags()
            {
                var chart = new int[1024];
                chart[157] = 2;
                chart[158] = 2;

                return new FlagSnapshot
                {
                    Available = true,
                    Source = "SAVEWK_ADR",
                    SaveLoaded = true,
                    ScenarioFlags = new[] { 240, 0, 0 },
                    BoxFlags = new[] { 0, 0 },
                    ScenarioChartState = chart,
                    EventFlags = new[] { 0 },
                    EventTemp = new[] { 0, 1 },
                    EventBattle = new[] { 0 },
                    Cfg1 = new[] { 1, 2 },
                    SetScenarioFlagCount = 1,
                    SetScenarioChartCount = 2
                };
            }

            public StateSnapshot CaptureState()
            {
                return new StateSnapshot
                {
                    Available = true,
                    Source = "SAVEWK_ADR",
                    SaveLoaded = true,
                    ImfName = "imf1b0",
                    MapId = 432,
                    AreaId = 26,
                    RegionId = 17,
                    X = 32,
                    Z = 5120,
                    Direction = 17,
                    PreviousMapId = 775
                };
            }
        }

        private sealed class BlockingScreenshotProbe : IScreenshotProbe
        {
            private readonly TaskCompletionSource<bool> _started =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Started => _started.Task;

            public void Release()
            {
                _release.TrySetResult(true);
            }

            public async Task<ScreenshotSnapshot> CaptureAsync(
                CancellationToken cancellationToken)
            {
                _started.TrySetResult(true);
                await _release.Task.WaitAsync(cancellationToken);

                return new ScreenshotSnapshot
                {
                    Available = true,
                    Png = StubScreenshotProbe.Png,
                    Width = 1,
                    Height = 1
                };
            }
        }

        private sealed class StubScreenshotProbe : IScreenshotProbe
        {
            public static readonly byte[] Png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z4v8AAAAASUVORK5CYII=");

            public Task<ScreenshotSnapshot> CaptureAsync(
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new ScreenshotSnapshot
                {
                    Available = true,
                    Png = Png,
                    Width = 1,
                    Height = 1
                });
            }
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
