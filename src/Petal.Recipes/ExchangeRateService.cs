using System.IO;
using System.Net.Http;
using System.Text.Json;
using Petal.Core.Text;

namespace Petal.Recipes;

/// <summary>
/// Today's exchange rates, fetched only when someone asks to convert an amount (Circle Anything's currency petal) from
/// ExchangeRate-API's open feed, and kept for twelve hours. The request is the same for everyone: the dollar's rates.
/// Nothing about the user or what they circled is sent.
/// </summary>
public static class ExchangeRateService
{
    public const string Address = "https://open.er-api.com/v6/latest/USD";

    /// <summary>The credit the feed asks for, shown with the rates.</summary>
    public const string Credit = "Rates By Exchange Rate API";

    public const string CreditLink = "https://www.exchangerate-api.com";

    private static readonly TimeSpan Fresh = TimeSpan.FromHours(12);

    private static readonly HttpClient Http = CreateClient();

    private static ExchangeRates? _rates;

    /// <summary>Where the rates are kept between runs (DropWheel's folder); a test sets its own.</summary>
    public static string CacheFile { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Petal", "rates.json");

    /// <summary>The rates already here and not yet twelve hours old, without going online; null otherwise.</summary>
    public static ExchangeRates? Cached
    {
        get
        {
            _rates ??= Load();
            return _rates is { } r && DateTime.UtcNow - r.Updated < Fresh ? r : null;
        }
    }

    /// <summary>The rates: those kept when fresh, otherwise fetched now (and kept). Throws when they can't be had.</summary>
    public static async Task<ExchangeRates> GetAsync(CancellationToken cancellationToken = default)
    {
        if (Cached is { } cached)
            return cached;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        string json;
        try
        {
            json = await Http.GetStringAsync(Address, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Offline: yesterday's rates are better than none, and say how old they are.
            if (_rates is { } old)
                return old;
            throw new InvalidOperationException("Today's exchange rates couldn't be fetched. Check the connection and try again.", ex);
        }
        var rates = Parse(json) ?? throw new InvalidOperationException("The exchange rates came back in a form DropWheel doesn't read.");
        _rates = rates;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            await File.WriteAllTextAsync(CacheFile, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory only.
        }
        return rates;
    }

    /// <summary>The feed's answer as rates, or null when it isn't one ("result" isn't "success", or there are no rates).</summary>
    internal static ExchangeRates? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("result", out var result) || result.GetString() != "success" || !root.TryGetProperty("rates", out var list))
                return null;
            var rates = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var rate in list.EnumerateObject())
                if (rate.Value.ValueKind == JsonValueKind.Number && rate.Value.GetDouble() > 0)
                    rates[rate.Name] = rate.Value.GetDouble();
            var updated = root.TryGetProperty("time_last_update_unix", out var unix) && unix.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                : DateTime.UtcNow;
            return rates.Count > 0 && rates.ContainsKey("USD") ? new ExchangeRates(updated, rates) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ExchangeRates? Load()
    {
        try
        {
            return File.Exists(CacheFile) ? Parse(File.ReadAllText(CacheFile)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Forgets the rates read so far (for tests that set <see cref="CacheFile"/>).</summary>
    internal static void Forget() => _rates = null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DropWheel/1.0");
        return client;
    }
}
