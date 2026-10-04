using System.Runtime.InteropServices;

namespace TimsahOffice.Core;

public sealed record DownloadAsset(string Url, string Sha256, long Size);
public sealed record ModelEntry(string Id, string Name, string FileName, string Url, string Sha256,
    long Size, string License, string Source);
public sealed record AssetCatalog(string EngineVersion, Dictionary<string, DownloadAsset> Engines, ModelEntry[] Models);
public sealed record OfficeSettings(string ModelId = "qwen3-0.6b", string? CustomModelPath = null,
    int Threads = 4, int ContextSize = 4096, int MaxTokens = 700, bool DirectRam = true, int IdleUnloadMinutes = 15)
{
    public void Validate(AssetCatalog catalog)
    {
        if (!catalog.Models.Any(m => m.Id == ModelId)) throw new ArgumentException("モデルを選択してください。");
        if (Threads < 1 || Threads > 128 || ContextSize < 2048 || ContextSize > 16384 || MaxTokens < 64 || MaxTokens > 2048
            || MaxTokens > ContextSize / 2 || IdleUnloadMinutes < 0 || IdleUnloadMinutes > 240) throw new ArgumentException("スレッド数・コンテキスト長・出力長の設定値が範囲外です。");
        if (CustomModelPath is not null && (!Path.IsPathFullyQualified(CustomModelPath) || !File.Exists(CustomModelPath)
            || !CustomModelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("カスタムモデルには実在するGGUFファイルの絶対パスを指定してください。");
    }
}

public sealed class OfficePaths
{
    public string Bundle { get; }
    public string UserData { get; }
    public string Settings => Path.Combine(UserData, "settings.json");
    public string Rules => Path.Combine(UserData, "rules.json");
    public string SeedRules => Path.Combine(Bundle, "data", "rules", "snapshot.json");
    public string Models => Path.Combine(UserData, "models");
    public string Engine => Path.Combine(UserData, "engine");
    public string EngineLog => Path.Combine(UserData, "logs", "llama-server.log");
    public string Catalog => Path.Combine(Bundle, "config", "catalog.json");
    public OfficePaths(string bundle, string? userData = null)
    {
        Bundle = Path.GetFullPath(bundle);
        UserData = Path.GetFullPath(userData ?? Environment.GetEnvironmentVariable("TIMSAH_OFFICE_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Timsah-Office"));
        Directory.CreateDirectory(UserData); Directory.CreateDirectory(Models);
        Directory.CreateDirectory(Path.GetDirectoryName(EngineLog)!);
    }
    public static string Rid => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")
        + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "-arm64" : "-x64");
    public string? FindEngine() => new[] { Path.Combine(Bundle, "engine"), Engine }
        .Where(Directory.Exists).SelectMany(p => Directory.EnumerateFiles(p,
            OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server", SearchOption.AllDirectories)).FirstOrDefault();
    public string? FindModel(ModelEntry entry) => new[] { Path.Combine(Bundle, "models", entry.FileName), Path.Combine(Models, entry.FileName) }
        .FirstOrDefault(File.Exists);
}
