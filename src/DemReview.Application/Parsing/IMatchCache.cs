using DemReview.Domain;

namespace DemReview.Application.Parsing;

/// <summary>Кеш разобранных демок. Ключ — путь к файлу, его размер и время изменения.</summary>
public interface IMatchCache
{
    bool TryGet(DemoFileKey key, out MatchData match);
    void Store(DemoFileKey key, MatchData match);
}

/// <summary>Идентификатор версии файла демки.</summary>
public sealed record DemoFileKey(string FullPath, long Length, DateTime LastWriteTimeUtc, int SampleTickStep = 16)
{
    public static DemoFileKey FromFile(string path, int sampleTickStep = 16)
    {
        var info = new FileInfo(path);
        return new DemoFileKey(info.FullName, info.Length, info.LastWriteTimeUtc, sampleTickStep);
    }
}
