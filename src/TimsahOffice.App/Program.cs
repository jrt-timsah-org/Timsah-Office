using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using TimsahOffice.App;
using TimsahOffice.Core;

string? Option(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var bundle = Option("--bundle") ?? AppContext.BaseDirectory;
var paths = new OfficePaths(bundle, Option("--data"));
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Timsah-Office/0.1.0");
if (args.Contains("--refresh-rules"))
{
    var corpus = await RuleImporter.DownloadAsync(http);
    await JsonStorage.WriteAsync(Option("--output") ?? paths.SeedRules, corpus);
    Console.WriteLine($"Validated {corpus.Sections.Length} sections: " + string.Join(", ", corpus.Sources.Select(s => s.Version)));
    return;
}
if (args.Contains("--version")) { Console.WriteLine("Timsah-Office 0.1.0 / .NET " + Environment.Version); return; }
using var instance = new FileStream(Path.Combine(paths.UserData, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var catalog = JsonStorage.Read<AssetCatalog>(paths.Catalog);
var state = new OfficeState(paths, catalog);
var notes = new NoteStore(paths.UserData);
await using var engine = new EngineHost(paths, catalog);
var installer = new AssetInstaller(http, paths, catalog);
var harness = new AssistantHarness(engine, notes);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = paths.Bundle, WebRootPath = Path.Combine(paths.Bundle, "wwwroot") });
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_000_000);
if (!int.TryParse(Option("--port") ?? "5273", out var port) || port is < 0 or > 65535) throw new ArgumentException("--port は0〜65535です。");
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Services.AddSingleton<OfficeJobs>();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();
var jobs = app.Services.GetRequiredService<OfficeJobs>();
var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
string? origin = null;
var wireJson = new JsonSerializerOptions(JsonStorage.Options) { WriteIndented = false };
app.Use(async (context, next) => {
    var req = context.Request;
    if (req.Host.Host != "127.0.0.1" || (origin is not null && req.Host.Value != new Uri(origin).Authority))
    { context.Response.StatusCode = 403; return; }
    if (req.Headers.TryGetValue("Origin", out var requestOrigin) && requestOrigin != origin)
    { context.Response.StatusCode = 403; return; }
    if (req.Headers["Sec-Fetch-Site"] == "cross-site") { context.Response.StatusCode = 403; return; }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (req.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if (req.Path != "/api/session" && req.Headers["X-Office-Token"] != token)
        { context.Response.StatusCode = 403; return; }
    }
    try { await next(context); }
    catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or NoteConflictException or InvalidOperationException)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = ex is NoteConflictException ? 409 : ex is KeyNotFoundException ? 404 : ex is InvalidOperationException ? 409 : 400;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/api/session", () => new { token, name = "Timsah-Office", identity = AssistantHarness.Identity });
app.MapGet("/api/status", () => new {
    engine = engine.Status, job = jobs.Status, state.Settings,
    models = catalog.Models.Select(m => new { m.Id, m.Name, m.Size, m.License, m.Source, installed = paths.FindModel(m) is not null }),
    engineInstalled = paths.FindEngine() is not null, catalog.EngineVersion,
    sources = state.Index.Corpus.Sources, sectionCount = state.Index.Corpus.Sections.Length,
    dataDirectory = paths.UserData, logPath = paths.EngineLog
});
app.MapPut("/api/settings", async (OfficeSettings settings, CancellationToken ct) => {
    if (engine.Status.State is "ready" or "starting" || jobs.Status.State == "running")
        throw new InvalidOperationException("モデルとバックグラウンド処理を停止してから設定を保存してください。");
    await state.SaveSettingsAsync(settings, ct); return Results.Ok(settings);
});
app.MapPost("/api/install", () => {
    if (engine.Status.State is "ready" or "starting") throw new InvalidOperationException("モデルを停止してから取得してください。");
    var modelId = state.Settings.ModelId;
    jobs.Start("install", (progress, ct) => installer.InstallAsync(modelId, progress, ct)); return Results.Accepted();
});
app.MapPost("/api/engine/start", () => {
    jobs.Start("start", (_, ct) => engine.StartAsync(state.Settings, ct)); return Results.Accepted();
});
app.MapPost("/api/engine/stop", () => {
    jobs.Start("stop", (_, ct) => engine.StopAsync(ct)); return Results.Accepted();
});
app.MapPost("/api/jobs/cancel", () => { jobs.Cancel(); return Results.Ok(); });
app.MapPost("/api/rules/update", () => {
    jobs.Start("rules", async (progress, ct) => { progress.Report(new("公式出典を取得・検証中")); await state.UpdateRulesAsync(http, ct); });
    return Results.Accepted();
});
app.MapGet("/api/rules/search", (string q, string? source) => {
    if (q.Length > 4000) throw new ArgumentException("検索文字列が長すぎます。");
    return state.Index.Search(q, 15, string.IsNullOrEmpty(source) ? null : source);
});
app.MapGet("/api/rules/sections", () => state.Index.Corpus.Sections.Select(s => new { s.Id, s.SourceId, s.Number, s.Title }));
app.MapGet("/api/rules/section", (string id) => state.Index.Corpus.Sections.FirstOrDefault(s => s.Id == id) is { } section ? Results.Ok(section) : Results.NotFound());
app.MapGet("/api/notes", (string? q) => string.IsNullOrWhiteSpace(q) ? notes.List() : notes.Search(q));
app.MapGet("/api/notes/{id}", (string id) => notes.Get(id) is { } page ? Results.Ok(page) : Results.NotFound());
app.MapPost("/api/notes", async (CreateNote request, CancellationToken ct) => Results.Ok(await notes.CreateAsync(request.ParentId, ct)));
app.MapPut("/api/notes/{id}", async (string id, NoteDraft draft, CancellationToken ct) => Results.Ok(await notes.UpdateAsync(id, draft, ct)));
app.MapDelete("/api/notes/{id}", async (string id, int revision, CancellationToken ct) => { await notes.DeleteAsync(id, revision, ct); return Results.NoContent(); });
app.MapGet("/api/notes/{id}/export", (string id) => notes.Get(id) is { } page
    ? Results.File(System.Text.Encoding.UTF8.GetBytes("# " + page.Title + "\n\n" + page.Markdown), "text/markdown; charset=utf-8", "timsah-note-" + page.Id + ".md") : Results.NotFound());
app.MapPost("/api/chat", async (ChatRequest request, HttpContext context) => {
    AssistantHarness.Validate(request);
    context.Response.ContentType = "text/event-stream"; context.Response.Headers.CacheControl = "no-cache";
    async Task Emit(HarnessEvent e)
    {
        await context.Response.WriteAsync("event: " + e.Type + "\ndata: " + JsonSerializer.Serialize(e.Data, wireJson) + "\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
    using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
    try { await harness.StreamAsync(request, state.Index, state.Settings, Emit, requestLifetime.Token); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex) { if (!context.RequestAborted.IsCancellationRequested) await Emit(new("error", ex.Message)); }
});
app.MapPost("/api/shutdown", (IHostApplicationLifetime lifetime) => { lifetime.StopApplication(); return Results.Ok(); });
await app.StartAsync();
origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
Console.WriteLine($"Timsah-Office: {origin}"); Console.WriteLine("データ: " + paths.UserData);
if (!args.Contains("--no-browser"))
{
    try
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(origin) { UseShellExecute = true });
        else Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { ArgumentList = { origin }, UseShellExecute = false });
    }
    catch (Exception ex) { Console.WriteLine("ブラウザを開けませんでした: " + ex.Message); }
}
if (!args.Contains("--no-autostart") && paths.FindEngine() is not null
    && (state.Settings.CustomModelPath is not null || paths.FindModel(catalog.Models.Single(m => m.Id == state.Settings.ModelId)) is not null))
    jobs.Start("start", (_, ct) => engine.StartAsync(state.Settings, ct));
var idleTask = Task.Run(async () => {
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
    try { while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping)) await engine.UnloadIfIdleAsync(state.Settings.IdleUnloadMinutes, app.Lifetime.ApplicationStopping); }
    catch (OperationCanceledException) { }
});
try { await app.WaitForShutdownAsync(); await idleTask; }
finally { await jobs.DrainAsync(); await engine.StopAsync(); }
public sealed record CreateNote(string? ParentId);
