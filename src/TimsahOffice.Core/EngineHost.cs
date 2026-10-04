using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace TimsahOffice.Core;

public sealed record EngineStatus(string State, string? Model, string? Error, int? ProcessId, long WorkingSetBytes);

/// <summary>Owns a single authenticated, loopback-only llama-server child; chat and lifecycle share a gate.</summary>
public sealed class EngineHost(OfficePaths paths, AssetCatalog catalog, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private DateTimeOffset lastActivity;
    public void Touch() { lock (sync) lastActivity = clock.GetUtcNow(); }
    public SemaphoreSlim Gate { get; } = new(1, 1);
    private Process? process;
    private string state = "stopped", model = "", error = "";
    private readonly object sync = new();
    private Task? outputPump, errorPump;
    private HttpClient? client;
    private readonly string apiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public HttpClient Client => client ?? throw new InvalidOperationException("モデルを起動してください。");
    public EngineStatus Status
    {
        get
        {
            lock (sync)
            {
                var running = process is { HasExited: false };
                if (!running && state == "ready") { state = "error"; error = "推論プロセスが終了しました。再起動してください。"; }
                long memory = 0;
                if (running) { try { process!.Refresh(); memory = process.WorkingSet64; } catch (InvalidOperationException) { } }
                return new(state, model, string.IsNullOrEmpty(error) ? null : error, running ? process!.Id : null, memory);
            }
        }
    }

    public async Task StartAsync(OfficeSettings settings, CancellationToken ct)
    {
        settings.Validate(catalog);
        await Gate.WaitAsync(ct);
        try
        {
            await StopInternalAsync();
            var binary = paths.FindEngine() ?? throw new FileNotFoundException("同梱エンジンが見つかりません。「モデルを取得」で準備してください。");
            var entry = catalog.Models.Single(m => m.Id == settings.ModelId);
            var modelPath = settings.CustomModelPath ?? paths.FindModel(entry)
                ?? throw new FileNotFoundException("モデルが見つかりません。「モデルを取得」で準備してください。");
            using (var stream = File.OpenRead(modelPath))
            {
                var magic = new byte[4];
                if (stream.Read(magic) != 4 || !magic.AsSpan().SequenceEqual("GGUF"u8)) throw new InvalidDataException("有効なGGUFモデルではありません。");
            }
            var port = FreePort();
            var info = new ProcessStartInfo(binary) {
                WorkingDirectory = Path.GetDirectoryName(binary)!, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var arg in new[] { "--model", modelPath, "--host", "127.0.0.1", "--port", port.ToString(),
                "--ctx-size", settings.ContextSize.ToString(), "--threads", settings.Threads.ToString(),
                "--threads-batch", settings.Threads.ToString(), "--parallel", "1", "--n-gpu-layers", "0",
                "--load-mode", settings.DirectRam ? "none" : "mmap", "--lazy-mode", "off",
                "--jinja", "--reasoning", "off", "--chat-template-kwargs", "{\"enable_thinking\":false}", "--no-webui", "--no-slots" }) info.ArgumentList.Add(arg);
            info.Environment["LLAMA_API_KEY"] = apiKey;
            lock (sync) { state = "starting"; error = ""; model = settings.CustomModelPath is null ? entry.Name : Path.GetFileName(modelPath); }
            var child = Process.Start(info) ?? throw new IOException("llama-serverを起動できません。");
            lock (sync) process = child;
            await File.WriteAllTextAsync(paths.EngineLog, "Timsah-Office / llama.cpp " + catalog.EngineVersion + Environment.NewLine, ct);
            outputPump = PumpAsync(child.StandardOutput);
            errorPump = PumpAsync(child.StandardError);
            client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(TimeSpan.FromMinutes(3));
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (child.HasExited) throw new IOException($"llama-serverが終了しました（{child.ExitCode}）。ログ: {paths.EngineLog}");
                try
                {
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(startup.Token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    using var response = await client.GetAsync("health", requestTimeout.Token);
                    if (response.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!startup.IsCancellationRequested) { }
                await Task.Delay(200, startup.Token);
            }
            lock (sync) { state = "ready"; lastActivity = clock.GetUtcNow(); }
        }
        catch (Exception ex)
        {
            await StopInternalAsync();
            lock (sync) { state = "error"; error = ex is OperationCanceledException ? "モデル起動を中断しました。" : ex.Message; }
            throw;
        }
        finally { Gate.Release(); }
    }

    public async Task<bool> UnloadIfIdleAsync(int minutes, CancellationToken ct = default)
    {
        if (minutes == 0 || !await Gate.WaitAsync(0, ct)) return false;
        try
        {
            lock (sync)
                if (state != "ready" || clock.GetUtcNow() - lastActivity < TimeSpan.FromMinutes(minutes)) return false;
            await StopInternalAsync();
            lock (sync) state = "unloaded";
            return true;
        }
        finally { Gate.Release(); }
    }
    public async Task StopAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try { await StopInternalAsync(); }
        finally { Gate.Release(); }
    }
    private async Task StopInternalAsync()
    {
        Process? child;
        lock (sync) { child = process; process = null; state = "stopped"; }
        if (child is not null)
        {
            if (!child.HasExited) { try { child.Kill(true); } catch (InvalidOperationException) { } }
            await child.WaitForExitAsync();
            if (outputPump is not null) await outputPump;
            if (errorPump is not null) await errorPump;
            child.Dispose();
        }
        client?.Dispose(); client = null;
    }
    private readonly SemaphoreSlim logGate = new(1, 1);
    private async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            await logGate.WaitAsync();
            try
            {
                // Bound logs; prompts and completions are never explicitly logged by the harness.
                if (new FileInfo(paths.EngineLog).Length > 2 * 1024 * 1024) File.Move(paths.EngineLog, paths.EngineLog + ".1", true);
                await File.AppendAllTextAsync(paths.EngineLog, line.Replace(apiKey, "[redacted]", StringComparison.Ordinal) + Environment.NewLine);
            }
            catch (IOException) { }
            finally { logGate.Release(); }
        }
    }
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    public async ValueTask DisposeAsync() { await StopAsync(); Gate.Dispose(); logGate.Dispose(); }
}
