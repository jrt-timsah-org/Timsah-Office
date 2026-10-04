using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace TimsahOffice.Core;

public sealed record InstallProgress(string Stage, long Received = 0, long Total = 0);

public sealed class AssetInstaller(HttpClient http, OfficePaths paths, AssetCatalog catalog)
{
    public async Task InstallAsync(string modelId, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var model = catalog.Models.Single(m => m.Id == modelId);
        if (paths.FindEngine() is null)
        {
            if (!catalog.Engines.TryGetValue(OfficePaths.Rid, out var engine)) throw new NotSupportedException("このOS/CPU用の配布エンジンがありません。");
            var archive = Path.Combine(paths.UserData, "engine-" + Guid.NewGuid().ToString("N") + (engine.Url.EndsWith(".zip") ? ".zip" : ".tar.gz"));
            var staging = archive + ".extract";
            try
            {
                await DownloadAsync(engine.Url, engine.Sha256, engine.Size, archive, "エンジン", progress, ct);
                Directory.CreateDirectory(staging);
                Extract(archive, staging);
                if (Directory.Exists(paths.Engine)) Directory.Delete(paths.Engine, true);
                Directory.Move(staging, paths.Engine);
            }
            finally
            {
                if (File.Exists(archive)) File.Delete(archive);
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }
        if (paths.FindModel(model) is null)
            await DownloadAsync(model.Url, model.Sha256, model.Size, Path.Combine(paths.Models, model.FileName), model.Name, progress, ct);
        progress.Report(new("準備完了"));
    }

    public async Task DownloadAsync(string url, string sha256, long expectedSize, string destination,
        string label, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".part";
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using (var output = File.Create(temp))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024]; long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    received += count;
                    if (received > expectedSize) throw new InvalidDataException("ダウンロードサイズがカタログと一致しません。");
                    hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    progress.Report(new(label + "を取得中", received, expectedSize));
                }
                if (received != expectedSize || !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256またはサイズの検証に失敗しました。ファイルは有効化されません。");
            }
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void Extract(string archive, string target)
    {
        // Hash-verified official archives only. BCL extraction rejects traversal outside the destination.
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ZipFile.ExtractToDirectory(archive, target);
        else
        {
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, target, false);
        }
        if (!OperatingSystem.IsWindows())
            foreach (var file in Directory.EnumerateFiles(target, "llama-server", SearchOption.AllDirectories))
                File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}
