using System.Text.RegularExpressions;

namespace DemReview.Application.Players;

/// <summary>Разбор SteamID, который пользователь вводит вручную: SteamID64, ссылка на профиль, [U:1:…], STEAM_X:Y:Z.</summary>
public static partial class SteamIds
{
    /// <summary>SteamID64 = база + account id (для индивидуальных аккаунтов).</summary>
    private const ulong IndividualBase = 76561197960265728;

    private const ulong MaxAccountId = uint.MaxValue;

    public static bool TryParse(string? input, out ulong steamId64)
    {
        steamId64 = 0;
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
            return false;

        // https://steamcommunity.com/profiles/7656119…/ — берём число из ссылки.
        if (ProfileUrl().Match(text) is { Success: true } url)
            text = url.Groups[1].Value;

        if (ulong.TryParse(text, out var value))
            return TryFromAccountOr64(value, out steamId64);

        if (SteamId3().Match(text) is { Success: true } id3)
            return TryFromAccountId(ulong.Parse(id3.Groups[1].Value), out steamId64);

        if (SteamId2().Match(text) is { Success: true } id2)
        {
            var account = ulong.Parse(id2.Groups[2].Value) * 2 + ulong.Parse(id2.Groups[1].Value);
            return TryFromAccountId(account, out steamId64);
        }

        return false;
    }

    /// <summary>Ссылка вида /id/имя — её нельзя превратить в SteamID без запроса к Steam.</summary>
    public static bool IsVanityUrl(string? input) => input is not null && VanityUrl().IsMatch(input.Trim());

    private static bool TryFromAccountOr64(ulong value, out ulong steamId64)
    {
        steamId64 = 0;
        if (value > IndividualBase && value - IndividualBase <= MaxAccountId)
        {
            steamId64 = value;
            return true;
        }
        return false;
    }

    private static bool TryFromAccountId(ulong account, out ulong steamId64)
    {
        steamId64 = 0;
        if (account is 0 or > MaxAccountId)
            return false;
        steamId64 = IndividualBase + account;
        return true;
    }

    [GeneratedRegex(@"steamcommunity\.com/profiles/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileUrl();

    [GeneratedRegex(@"steamcommunity\.com/id/[^/\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex VanityUrl();

    [GeneratedRegex(@"^\[?U:1:(\d+)\]?$", RegexOptions.IgnoreCase)]
    private static partial Regex SteamId3();

    [GeneratedRegex(@"^STEAM_[0-5]:([01]):(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SteamId2();
}
