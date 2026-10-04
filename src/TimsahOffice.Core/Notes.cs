namespace TimsahOffice.Core;

public sealed record NotePage(string Id, string Title, string Markdown, string? ParentId, bool Favorite,
    int Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record NoteDraft(string Title, string Markdown, string? ParentId, bool Favorite, int Revision);
public sealed record NoteBook(int SchemaVersion, NotePage[] Pages);

/// <summary>Atomic local pages with optimistic revisions and one recoverable backup per write.</summary>
public sealed class NoteStore
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private NoteBook book;
    public NoteStore(string userData)
    {
        path = Path.Combine(userData, "notes.json");
        book = File.Exists(path) ? JsonStorage.Read<NoteBook>(path) : new(1, []);
        if (book.SchemaVersion != 1) throw new InvalidDataException("未対応のメモ形式です。");
    }
    public NotePage[] List() => Volatile.Read(ref book).Pages.OrderByDescending(p => p.UpdatedAt).ToArray();
    public NotePage? Get(string id) => Volatile.Read(ref book).Pages.FirstOrDefault(p => p.Id == id);
    public async Task<NotePage> CreateAsync(string? parentId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (parentId is not null && !book.Pages.Any(p => p.Id == parentId)) throw new ArgumentException("親ページがありません。");
            var now = DateTimeOffset.UtcNow;
            var page = new NotePage(Guid.NewGuid().ToString("N"), "無題のページ", "", parentId, false, 1, now, now);
            await SaveAsync(new(1, [.. book.Pages, page]), ct); return page;
        }
        finally { gate.Release(); }
    }
    public async Task<NotePage> UpdateAsync(string id, NoteDraft draft, CancellationToken ct)
    {
        if (draft.Title.Length > 200 || draft.Markdown.Length > 200_000) throw new ArgumentException("タイトルまたは本文が長すぎます。");
        await gate.WaitAsync(ct);
        try
        {
            var page = Get(id) ?? throw new KeyNotFoundException("ページがありません。");
            if (page.Revision != draft.Revision) throw new NoteConflictException("別の画面で更新されました。再読込して確認してください。");
            if (draft.ParentId is not null)
            {
                var parent = Get(draft.ParentId) ?? throw new ArgumentException("親ページがありません。");
                for (var current = parent; current is not null; current = current.ParentId is null ? null : Get(current.ParentId))
                    if (current.Id == id) throw new ArgumentException("ページ階層が循環します。");
            }
            var updated = page with { Title = string.IsNullOrWhiteSpace(draft.Title) ? "無題のページ" : draft.Title.Trim(),
                Markdown = draft.Markdown, ParentId = draft.ParentId, Favorite = draft.Favorite,
                Revision = page.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
            await SaveAsync(new(1, book.Pages.Select(p => p.Id == id ? updated : p).ToArray()), ct);
            return updated;
        }
        finally { gate.Release(); }
    }
    public async Task DeleteAsync(string id, int revision, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var page = Get(id) ?? throw new KeyNotFoundException("ページがありません。");
            if (page.Revision != revision) throw new NoteConflictException("ページが更新されています。再読込してください。");
            if (book.Pages.Any(p => p.ParentId == id)) throw new ArgumentException("子ページを移動または削除してから削除してください。");
            await SaveAsync(new(1, book.Pages.Where(p => p.Id != id).ToArray()), ct);
        }
        finally { gate.Release(); }
    }
    public NotePage[] Search(string query) => List().Where(p =>
        RuleIndex.Normalize(p.Title + " " + p.Markdown).Contains(RuleIndex.Normalize(query), StringComparison.Ordinal)).Take(30).ToArray();
    private async Task SaveAsync(NoteBook updated, CancellationToken ct)
    {
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        await JsonStorage.WriteAsync(path, updated, ct);
        Volatile.Write(ref book, updated);
    }
}
public sealed class NoteConflictException(string message) : Exception(message);
