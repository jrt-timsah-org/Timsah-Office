using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TimsahOffice.Core;

public sealed record RuleSource(string Id, string Title, string Version, string Url, string Revision,
    string Sha256, DateTimeOffset RetrievedAt);
public sealed record RuleSection(string Id, string SourceId, string Number, string Title, string Text,
    string Url, string[] Related);
public sealed record RuleCorpus(int SchemaVersion, RuleSource[] Sources, RuleSection[] Sections);
public sealed record SearchHit(RuleSection Section, double Score);

public static class JsonStorage
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"JSONを読み込めません: {path}");
    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, Options), ct);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public static partial class RuleImporter
{
    public const string NaviUrl = "https://core.scramble-robot.org/rule/core-2-rulenavi/";
    public const string Repository = "scramble-robot/CoRE-Rulebook";
    public const string PinnedRevision = "dc584ffffa58028774b92454f292de9386ef1ea8";

    public static async Task<RuleCorpus> DownloadAsync(HttpClient client, CancellationToken ct = default)
    {
        // All three sources must validate before replacing the active snapshot.
        var html = await client.GetStringAsync(NaviUrl, ct);
        var revisionJson = await client.GetStringAsync($"https://api.github.com/repos/{Repository}/commits/main", ct);
        using var revisionDoc = JsonDocument.Parse(revisionJson);
        var revision = revisionDoc.RootElement.GetProperty("sha").GetString()!;
        if (!Regex.IsMatch(revision, "^[a-f0-9]{40}$")) throw new InvalidDataException("上流のコミットIDが不正です。");
        var common = await client.GetStringAsync($"https://raw.githubusercontent.com/{Repository}/{revision}/core_common_rulebook.md", ct);
        var system = await client.GetStringAsync($"https://raw.githubusercontent.com/{Repository}/{revision}/core_gamesystem_rulebook.md", ct);
        var sources = new List<RuleSource>();
        var sections = new List<RuleSection>();
        var navi = ParseNavi(html);
        sources.Add(navi.Source); sections.AddRange(navi.Sections);
        foreach (var (id, title, name, text) in new[] {
            ("common", "CoRE共通ルールブック", "core_common_rulebook.md", common),
            ("system", "CoRE競技システムルールブック", "core_gamesystem_rulebook.md", system) })
        {
            var url = $"https://github.com/{Repository}/blob/{revision}/{name}";
            var version = VersionRegex().Match(text).Value;
            if (string.IsNullOrEmpty(version)) throw new InvalidDataException($"{title}の版を抽出できません。");
            sources.Add(new(id, title, version, url, revision, Hash(text), DateTimeOffset.UtcNow));
            sections.AddRange(ParseMarkdown(id, text, url));
        }
        var corpus = new RuleCorpus(1, sources.ToArray(), sections.ToArray());
        Validate(corpus);
        return corpus;
    }

    public static (RuleSource Source, RuleSection[] Sections) ParseNavi(string html)
    {
        const string marker = "window.RB = ";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException("ルールナビの構造が変わりました。既存のデータを保持します。");
        var bytes = Encoding.UTF8.GetBytes(html[(start + marker.Length)..]);
        var reader = new Utf8JsonReader(bytes);
        using var doc = JsonDocument.ParseValue(ref reader);
        var raw = doc.RootElement;
        var meta = raw.GetProperty("meta");
        var source = new RuleSource("core2", "CoRE-2ルールナビ", meta.GetProperty("ver").GetString()!,
            NaviUrl, meta.GetProperty("date").GetString()!, Hash(html), DateTimeOffset.UtcNow);
        var result = new List<RuleSection>();
        foreach (var sec in raw.GetProperty("secs").EnumerateArray())
        {
            var number = sec.GetProperty("id").GetString()!;
            var title = sec.GetProperty("title").GetString()!;
            var text = new StringBuilder();
            foreach (var block in sec.GetProperty("b").EnumerateArray())
            {
                if (block.TryGetProperty("x", out var x)) text.AppendLine(Plain(x.GetString() ?? ""));
                if (block.TryGetProperty("items", out var items))
                    foreach (var item in items.EnumerateArray()) text.AppendLine("・" + Plain(item.GetString() ?? ""));
                if (block.TryGetProperty("cap", out var cap)) text.AppendLine(Plain(cap.GetString() ?? ""));
                if (block.TryGetProperty("rows", out var rows))
                    foreach (var row in rows.EnumerateArray()) text.AppendLine(string.Join(" | ", row.EnumerateArray().Select(c => Plain(c.ToString()))));
                if (block.TryGetProperty("num", out var num)) text.AppendLine("[図: " + num.GetString() + "。形状は原典の図で確認]");
            }
            // Explanatory summaries are not silently promoted to normative provisions.
            var body = text.ToString().Trim();
            if (body.Length == 0) continue;
            result.Add(new("core2:" + number, "core2", number, title, body,
                NaviUrl + "#r-" + number,
                sec.TryGetProperty("rel", out var rel) ? rel.EnumerateArray().Select(r => r.GetString()!).ToArray() : []));
        }
        return (source, result.ToArray());
    }

    public static IEnumerable<RuleSection> ParseMarkdown(string sourceId, string markdown, string url)
    {
        var matches = HeadingRegex().Matches(markdown);
        var hierarchy = new List<string>();
        var chapter = "";
        var duplicates = new Dictionary<string, int>();
        foreach (Match heading in matches)
        {
            var level = heading.Groups[1].Length;
            var title = heading.Groups[2].Value.Trim();
            while (hierarchy.Count >= level) hierarchy.RemoveAt(hierarchy.Count - 1);
            hierarchy.Add(title);
            if (level == 1) chapter = Regex.Match(title, @"\d+章").Value;
            var end = heading.NextMatch() is { Success: true } next ? next.Index : markdown.Length;
            var body = markdown[(heading.Index + heading.Length)..end].Trim();
            // Keep linked image descriptions, remove HTML wrappers, preserve units and tables.
            body = Plain(body);
            if (body.Length == 0) continue;
            var slug = Regex.Replace(title.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[^\p{L}\p{N}_\- ]", "").Replace(' ', '-');
            var count = duplicates.GetValueOrDefault(slug); duplicates[slug] = count + 1;
            if (count > 0) slug += "-" + count;
            yield return new(sourceId + ":" + heading.Index, sourceId, chapter,
                string.Join(" / ", hierarchy), body, url + "#" + Uri.EscapeDataString(slug), []);
        }
    }

    public static void Validate(RuleCorpus corpus)
    {
        if (corpus.SchemaVersion != 1 || corpus.Sources.Length != 3 || corpus.Sections.Length < 80
            || corpus.Sections.Length > 2000 || corpus.Sections.Select(s => s.Id).Distinct().Count() != corpus.Sections.Length
            || !new[] { "5.4", "5.6", "10.3" }.All(n => corpus.Sections.Any(s => s.SourceId == "core2" && s.Number == n))
            || corpus.Sections.Any(s => string.IsNullOrWhiteSpace(s.Text) || s.Text.Length > 100_000
                || !corpus.Sources.Any(source => source.Id == s.SourceId)))
            throw new InvalidDataException("ルールデータの検証に失敗しました。既存のデータを保持します。");
    }

    private static string Plain(string s) => WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", ""));
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    [GeneratedRegex(@"(?m)^(#{1,6})\s+(.+)$")] private static partial Regex HeadingRegex();
    [GeneratedRegex(@"[vV]\d+\.\d+\.\d+")] private static partial Regex VersionRegex();
}
