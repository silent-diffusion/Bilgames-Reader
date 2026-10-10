using Bilgames.Core.Model;
using Bilgames.Core.Storage;

namespace Bilgames.Core.Jobs;

/// <summary>
/// "Keep finished audio": which generated chapters stay on disk. Only chapter audio is ever removed —
/// never a book's text, analysis, cast or notes — and a removed chapter is made again when the
/// listener gets there, exactly like listening while it generates.
/// </summary>
public static class AudioRetention
{
    /// <summary>With <see cref="KeepAudio.Recent"/>: chapters kept behind and ahead of the one being listened to.</summary>
    public const int RecentBehind = 1, RecentAhead = 3;

    /// <summary>The included chapters whose audio should exist under <paramref name="policy"/>, in reading order.</summary>
    public static IReadOnlyList<int> Wanted(BookRecord book, KeepAudio policy)
    {
        var order = book.IncludedChapters.Select(c => c.Index).ToList();
        if (policy == KeepAudio.All || order.Count == 0) return order;
        // Where the listener is; a book not started yet is "at" its first chapter.
        int at = Math.Max(0, order.IndexOf(book.Position.ChapterIndex));
        return policy switch
        {
            KeepAudio.Recent => order.Skip(Math.Max(0, at - RecentBehind)).Take(RecentBehind + 1 + RecentAhead + Math.Min(0, at - RecentBehind)).ToList(),
            _ => order.Skip(at).ToList(), // UntilListened: the current chapter and everything after it
        };
    }

    /// <summary>
    /// Removes finished audio of chapters <paramref name="policy"/> doesn't keep (never <paramref name="playing"/>),
    /// and records them on the book so they show as removed rather than never made. Returns how many.
    /// </summary>
    public static int Evict(LibraryStore store, string bookId, KeepAudio policy, int playing = -1)
    {
        if (policy == KeepAudio.All) return 0;
        lock (store.EditLock(bookId))
        {
            var book = store.LoadBook(bookId);
            if (book is null) return 0;
            var keep = Wanted(book, policy).ToHashSet();
            int removed = 0;
            foreach (var info in book.IncludedChapters)
            {
                if (keep.Contains(info.Index) || info.Index == playing) continue;
                if (store.LoadChapterAudio(bookId, info.Index) is not { } audio) continue;
                TryDelete(store.ChapterAudioPath(bookId, audio));
                TryDelete(Path.Combine(store.BookDir(bookId), "audio", $"{info.Index}.json"));
                if (!book.Generation.Removed.Contains(info.Index)) book.Generation.Removed.Add(info.Index);
                removed++;
            }
            if (removed > 0) store.SaveBook(book);
            return removed;
        }
    }

    private static void TryDelete(string path)
    {
        // A file still open in the player is left for the next sweep.
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
