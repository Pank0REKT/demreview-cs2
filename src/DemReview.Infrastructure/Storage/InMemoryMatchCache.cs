using System.Collections.Concurrent;
using DemReview.Application;
using DemReview.Application.Parsing;
using DemReview.Domain;

namespace DemReview.Infrastructure.Storage;

/// <summary>Кеш разобранных демок в памяти на время работы приложения.</summary>
public sealed class InMemoryMatchCache : IMatchCache
{
    private readonly ConcurrentDictionary<string, (DemoFileKey Key, MatchData Match)> _items;

    public InMemoryMatchCache(IPlatformService platform)
    {
        // На Windows пути регистронезависимы, на Linux — регистрозависимы.
        _items = new ConcurrentDictionary<string, (DemoFileKey, MatchData)>(platform.PathComparer);
    }

    public bool TryGet(DemoFileKey key, out MatchData match)
    {
        if (_items.TryGetValue(key.FullPath, out var item) && item.Key.Length == key.Length &&
            item.Key.LastWriteTimeUtc == key.LastWriteTimeUtc)
        {
            match = item.Match;
            return true;
        }

        match = null!;
        return false;
    }

    public void Store(DemoFileKey key, MatchData match) => _items[key.FullPath] = (key, match);
}
