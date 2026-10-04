using System.Text;
using System.Text.RegularExpressions;

namespace TimsahOffice.Core;

/// <summary>Immutable, in-RAM BM25 index over Japanese bigrams and Latin words. No embedding process.</summary>
public sealed partial class RuleIndex
{
    private sealed record Document(RuleSection Section, Dictionary<string, int> Terms, int Length);
    private readonly Document[] documents;
    private readonly Dictionary<string, int> frequencies = new(StringComparer.Ordinal);
    private readonly double averageLength;
    public RuleCorpus Corpus { get; }
    private static readonly (string[] Keys, string[] Values)[] Synonyms = [
        (["サイズ", "寸法", "大きさ", "幅", "高さ"], ["大きさ", "ロボット", "幅", "高さ"]),
        (["クールダウン", "待機", "連続発動"], ["スキル", "効果", "5秒"]),
        (["復活", "リスポーン", "撃破"], ["撃破", "復活", "30秒"]),
        (["重量", "重さ", "kg"], ["質量", "重さ"]),
        (["射撃", "発射", "射出", "連射"], ["射出", "ディスク"]),
        (["ピット", "補給", "装填"], ["補給", "ピットイン"]),
        (["パネル", "オートレフェリー"], ["ダメージパネル", "オートレフェリー"])
    ];

    public RuleIndex(RuleCorpus corpus)
    {
        RuleImporter.Validate(corpus); Corpus = corpus;
        documents = corpus.Sections.Select(s => {
            var tokens = Terms(s.Title + " " + s.Title + " " + s.Text).ToArray();
            return new Document(s, tokens.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count()), tokens.Length);
        }).ToArray();
        foreach (var d in documents) foreach (var term in d.Terms.Keys) frequencies[term] = frequencies.GetValueOrDefault(term) + 1;
        averageLength = Math.Max(1, documents.Average(d => d.Length));
    }

    public SearchHit[] Search(string query, int limit = 8, string? source = null)
    {
        query = Normalize(query);
        if (string.IsNullOrWhiteSpace(query)) return [];
        var weighted = Terms(query).Distinct().ToDictionary(t => t, _ => 1d);
        foreach (var (keys, values) in Synonyms)
            if (keys.Any(k => query.Contains(k, StringComparison.OrdinalIgnoreCase)))
                foreach (var term in values.SelectMany(Terms).Distinct()) weighted.TryAdd(term, .35);
        var sectionNumber = Regex.Match(query, @"(?<!\d)(\d{1,2}\.\d{1,2})(?!\d)").Value;
        return documents.Where(d => source is null || d.Section.SourceId == source).Select(d => {
            double score = 0;
            foreach (var (term, weight) in weighted)
            {
                if (!d.Terms.TryGetValue(term, out var tf)) continue;
                var df = frequencies[term];
                var idf = Math.Log(1 + (documents.Length - df + .5) / (df + .5));
                score += weight * idf * tf * 2.2 / (tf + 1.2 * (.25 + .75 * d.Length / averageLength));
            }
            if (d.Section.SourceId == "core2") score *= 1.15;
            if (sectionNumber.Length > 0 && d.Section.SourceId == "core2" && d.Section.Number == sectionNumber) score += 100;
            if (query.Length > 1 && Normalize(d.Section.Title).Contains(query, StringComparison.Ordinal)) score += 10;
            return new SearchHit(d.Section, score);
        }).Where(h => h.Score > 0).OrderByDescending(h => h.Score).Take(Math.Clamp(limit, 1, 30)).ToArray();
    }

    public static string Normalize(string text) => text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
    private static IEnumerable<string> Terms(string text)
    {
        text = Normalize(text);
        foreach (Match m in WordRegex().Matches(text))
        {
            var word = m.Value;
            if (word[0] <= 127) { yield return word; continue; }
            if (word.Length == 1) { yield return word; continue; }
            for (var i = 0; i < word.Length - 1; i++) yield return word.Substring(i, 2);
        }
    }
    [GeneratedRegex(@"[a-z0-9]+(?:\.[0-9]+)?|[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}ー々]+")] private static partial Regex WordRegex();
}
