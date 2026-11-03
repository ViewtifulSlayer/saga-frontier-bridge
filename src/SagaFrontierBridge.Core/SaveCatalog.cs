using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SagaFrontierBridge.Core
{
    public sealed class SaveCatalog
    {
        public SaveCatalog(string? root = null)
        {
            Root = NormalizeRoot(root);
        }

        public string Root { get; }

        public SaveCatalogSnapshot Discover()
        {
            var errors = new List<string>();

            if (!Directory.Exists(Root))
            {
                return new SaveCatalogSnapshot
                {
                    Root = Root,
                    RootExists = false,
                    Saves = Array.Empty<SaveFileRecord>(),
                    Errors = errors
                };
            }

            var saves = new List<SaveFileRecord>();

            try
            {
                foreach (var path in Directory.EnumerateFiles(Root, "SaveData.bin", SearchOption.AllDirectories))
                {
                    try
                    {
                        var record = ReadRecord(path);
                        if (record != null)
                        {
                            saves.Add(record);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{path}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Root}: {ex.Message}");
            }

            saves.Sort((left, right) =>
                string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));

            return new SaveCatalogSnapshot
            {
                Root = Root,
                RootExists = true,
                Saves = saves,
                Errors = errors
            };
        }

        public SaveFileRecord? Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return Discover().Saves.FirstOrDefault(save =>
                string.Equals(save.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private SaveFileRecord? ReadRecord(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return null;
            }

            return new SaveFileRecord
            {
                Id = BuildId(fullPath),
                Path = fullPath,
                RelativePath = Path.GetRelativePath(Root, fullPath),
                Length = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                Sha256 = ComputeSha256(fullPath)
            };
        }

        private static string ComputeSha256(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        private static string BuildId(string fullPath)
        {
            var normalized = fullPath.ToLowerInvariant();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(hash).Substring(0, 12).ToLowerInvariant();
        }

        private static string NormalizeRoot(string? root)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                return Path.GetFullPath(root.Trim());
            }

            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
            {
                documents = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Documents");
            }

            return Path.GetFullPath(Path.Combine(
                documents,
                "My Games",
                "SaGa Frontier Remastered",
                "Steam"));
        }
    }

    public sealed class SaveCatalogSnapshot
    {
        public string Root { get; init; } = string.Empty;

        public bool RootExists { get; init; }

        public IReadOnlyList<SaveFileRecord> Saves { get; init; } = Array.Empty<SaveFileRecord>();

        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

        public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }

    public sealed class SaveFileRecord
    {
        public string Id { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        public string RelativePath { get; init; } = string.Empty;

        public long Length { get; init; }

        public DateTime LastWriteTimeUtc { get; init; }

        public string Sha256 { get; init; } = string.Empty;
    }
}
