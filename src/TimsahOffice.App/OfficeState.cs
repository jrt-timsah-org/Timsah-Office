using TimsahOffice.Core;
namespace TimsahOffice.App;

public sealed class OfficeState
{
    private RuleIndex index;
    private OfficeSettings settings;
    private readonly SemaphoreSlim settingsGate = new(1, 1);
    public OfficePaths Paths { get; }
    public AssetCatalog Catalog { get; }
    public RuleIndex Index => Volatile.Read(ref index);
    public OfficeSettings Settings => Volatile.Read(ref settings);
    public OfficeState(OfficePaths paths, AssetCatalog catalog)
    {
        Paths = paths; Catalog = catalog;
        index = new(JsonStorage.Read<RuleCorpus>(File.Exists(paths.Rules) ? paths.Rules : paths.SeedRules));
        settings = File.Exists(paths.Settings) ? JsonStorage.Read<OfficeSettings>(paths.Settings)
            : new(Threads: Math.Clamp(Environment.ProcessorCount / 2, 1, 8));
        settings.Validate(catalog);
    }
    public async Task SaveSettingsAsync(OfficeSettings value, CancellationToken ct)
    {
        value.Validate(Catalog); await settingsGate.WaitAsync(ct);
        try { await JsonStorage.WriteAsync(Paths.Settings, value, ct); Volatile.Write(ref settings, value); }
        finally { settingsGate.Release(); }
    }
    public async Task UpdateRulesAsync(HttpClient http, CancellationToken ct)
    {
        var corpus = await RuleImporter.DownloadAsync(http, ct);
        var next = new RuleIndex(corpus);
        await JsonStorage.WriteAsync(Paths.Rules, corpus, ct);
        Volatile.Write(ref index, next);
    }
}
public sealed record JobStatus(string State, string Kind, InstallProgress? Progress, string? Error);
public sealed class OfficeJobs(IHostApplicationLifetime lifetime)
{
    private readonly object sync = new();
    private JobStatus status = new("idle", "", null, null);
    private CancellationTokenSource? cancellation;
    private Task? task;
    public JobStatus Status { get { lock (sync) return status; } }
    public void Start(string kind, Func<IProgress<InstallProgress>, CancellationToken, Task> action)
    {
        lock (sync)
        {
            if (status.State == "running") throw new InvalidOperationException("バックグラウンド処理が実行中です。");
            cancellation?.Dispose(); cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            var ct = cancellation.Token;
            status = new("running", kind, new("開始中"), null);
            task = Task.Run(async () => {
                try
                {
                    await action(new JobProgress(p => { lock (sync) status = status with { Progress = p }; }), ct);
                    lock (sync) status = status with { State = "done", Progress = new("完了") };
                }
                catch (OperationCanceledException) { lock (sync) status = status with { State = "cancelled", Error = "処理を中断しました。" }; }
                catch (Exception ex) { lock (sync) status = status with { State = "error", Error = ex.Message }; }
            }, CancellationToken.None);
        }
    }
    public void Cancel() { lock (sync) cancellation?.Cancel(); }
    public async Task DrainAsync() { Cancel(); if (task is not null) await task; }
    private sealed class JobProgress(Action<InstallProgress> action) : IProgress<InstallProgress> { public void Report(InstallProgress value) => action(value); }
}
