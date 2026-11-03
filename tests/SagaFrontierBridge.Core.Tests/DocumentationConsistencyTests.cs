using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SagaFrontierBridge.Core;
using Xunit;

namespace SagaFrontierBridge.Core.Tests
{
    public sealed class DocumentationConsistencyTests
    {
        private const string SupportedGameVersion = "1.0.1";
        private const string SupportedUnityVersion = "2019.4.16f1";
        private const string PluginProjectPath =
            "src/SagaFrontierBridge.Plugin/SagaFrontierBridge.Plugin.csproj";
        private const string BuildPropsPath = "Directory.Build.props";

        private static readonly string RepositoryRoot = FindRepositoryRoot();

        [Fact]
        public void BridgeVersionMatchesReadmeChangelogApiAndBuildProps()
        {
            var buildProps = ReadProject(BuildPropsPath);
            var projectVersion = Assert.Single(
                    buildProps.Descendants(),
                    element => element.Name.LocalName == "Version")
                .Value
                .Trim();

            Assert.Equal(BridgeInfo.Version, projectVersion);
            Assert.Equal(
                new Version($"{BridgeInfo.Version}.0"),
                typeof(BridgeInfo).Assembly.GetName().Version);

            var readme = NormalizeWhitespace(Read("README.md"));
            var changelog = NormalizeWhitespace(Read("CHANGELOG.md"));
            var api = NormalizeWhitespace(Read("docs/api.md"));

            AssertContains(
                readme,
                $"The current version is `{BridgeInfo.Version}`",
                "README.md");
            AssertContains(changelog, $"## [{BridgeInfo.Version}]", "CHANGELOG.md");
            AssertContains(
                api,
                $"Server: saga-frontier-bridge/{BridgeInfo.Version}",
                "docs/api.md");
            AssertContains(
                api,
                $"\"version\": \"{BridgeInfo.Version}\"",
                "docs/api.md");
        }

        [Fact]
        public void GameAndUnityVersionsMatchSupportedTargetsAcrossDocs()
        {
            var readme = NormalizeWhitespace(Read("README.md"));
            var api = NormalizeWhitespace(Read("docs/api.md"));
            var savePath = NormalizeWhitespace(Read("docs/save-path.md"));

            AssertContains(readme, $"game {SupportedGameVersion}", "README.md");
            AssertContains(readme, $"Unity {SupportedUnityVersion}", "README.md");

            AssertContains(
                api,
                $"\"gameVersion\": \"{SupportedGameVersion}\"",
                "docs/api.md");
            AssertContains(
                api,
                $"\"unityVersion\": \"{SupportedUnityVersion}\"",
                "docs/api.md");

            AssertContains(
                savePath,
                $"SaGa Frontier Remastered {SupportedGameVersion}",
                "docs/save-path.md");
            AssertContains(
                savePath,
                $"Unity {SupportedUnityVersion}",
                "docs/save-path.md");
        }

        [Fact]
        public void BepInExPinMatchesReadmeAndPluginProject()
        {
            var pluginProject = ReadProject(PluginProjectPath);
            var package = Assert.Single(
                pluginProject.Descendants(),
                element =>
                    element.Name.LocalName == "PackageReference" &&
                    string.Equals(
                        (string?)element.Attribute("Include"),
                        "BepInEx.Unity.IL2CPP",
                        StringComparison.Ordinal));

            var version = Assert.Single(package.Attributes("Version"))
                .Value
                .Trim();

            AssertContains(
                NormalizeWhitespace(Read("README.md")),
                $"`{version}`",
                "README.md");
        }

        private static string Read(string relativePath)
        {
            return File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));
        }

        private static XDocument ReadProject(string relativePath)
        {
            return XDocument.Parse(Read(relativePath));
        }

        private static string NormalizeWhitespace(string text)
        {
            return Regex.Replace(text, @"\s+", " ");
        }

        private static void AssertContains(
            string text,
            string expected,
            string source)
        {
            Assert.True(
                text.Contains(expected, StringComparison.Ordinal),
                $"{source} does not contain: {expected}");
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
    }
}
