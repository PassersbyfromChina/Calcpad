using System.Globalization;
using System.Text.Json.Nodes;
using Calcpad.Core.Numerics;
using Calcpad.Core.Plugins;

namespace Calcpad.Plugins.CalculatorKit;

/// <summary>Where the numbers in a rate table come from.</summary>
public enum CurrencyRateSource
{
    /// <summary>Offline rates the user can edit. The default, and the only source that needs no network.</summary>
    Manual,

    /// <summary>Daily rates fetched over the network after the user explicitly turned this on.</summary>
    Daily
}

/// <summary>One currency and its value in US dollars.</summary>
public sealed record CurrencyQuote(string Code, double PerUsd);

/// <summary>Fetches a full rate table. Only ever constructed after the user opted in.</summary>
public interface ICurrencyRateProvider
{
    /// <summary>Human readable name of the source, shown next to the fetched timestamp.</summary>
    string Name { get; }

    /// <summary>Fetches today's rates in units per US dollar.</summary>
    ValueTask<IReadOnlyList<CurrencyQuote>> GetRatesAsync(CancellationToken ct = default);
}

/// <summary>
/// The converter's currency rates. Honesty rules, enforced by the type:
/// <list type="bullet">
/// <item>the default source is the editable manual table and it is always labelled as such;</item>
/// <item>daily rates are only used after the user enabled them, the plugin holds <c>Network</c> and a
/// fetch actually succeeded;</item>
/// <item>when daily rates were requested but cannot be fetched the converter reports
/// "rate unavailable" and refuses to convert - it never falls back to older or guessed numbers.</item>
/// </list>
/// </summary>
public sealed class CurrencyRateTable
{
    /// <summary>Category id used by the converter surface for currencies.</summary>
    public const string CategoryId = "currency";

    /// <summary>Label shown for the offline, user editable table.</summary>
    public const string ManualLabel = "手动汇率 / manual rate";

    /// <summary>Label shown when daily rates were requested but are not available.</summary>
    public const string UnavailableLabel = "汇率不可用 / rate unavailable";

    /// <summary>Settings key holding the manual overrides as JSON.</summary>
    public const string ManualSettingsKey = "currency.manualRates";

    /// <summary>Settings key holding the selected source.</summary>
    public const string SourceSettingsKey = "currency.source";

    private readonly Dictionary<string, double> _manual = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CurrencyQuote> _live = [];
    private readonly List<CurrencyRate> _currencies = [];

    /// <summary>Creates a table seeded from the currencies the math engine knows about.</summary>
    public CurrencyRateTable(IEnumerable<CurrencyRate>? seed = null)
    {
        foreach (var rate in seed ?? CurrencyRates.All)
        {
            _currencies.Add(rate);
            _manual[rate.Id] = rate.PerUsd;
        }
    }

    /// <summary>Selected source.</summary>
    public CurrencyRateSource Source { get; private set; } = CurrencyRateSource.Manual;

    /// <summary>When the live table was fetched, or null.</summary>
    public DateTimeOffset? FetchedAt { get; private set; }

    /// <summary>Last failure message, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>Name of the provider that produced the live table, or null.</summary>
    public string? ProviderName { get; private set; }

    /// <summary>True when daily rates were fetched successfully and may be used.</summary>
    public bool IsLive => Source == CurrencyRateSource.Daily && FetchedAt is not null && Error is null && _live.Count > 0;

    /// <summary>True when the user asked for daily rates but they are not usable.</summary>
    public bool IsUnavailable => Source == CurrencyRateSource.Daily && !IsLive;

    /// <summary>Label that must be shown wherever a currency result appears.</summary>
    public string SourceLabel => Source switch
    {
        CurrencyRateSource.Daily when IsLive => $"每日汇率 / daily rates · {FetchedAt:yyyy-MM-dd} · {ProviderName}",
        CurrencyRateSource.Daily => UnavailableLabel,
        _ => ManualLabel
    };

    /// <summary>Currencies known to this table.</summary>
    public IReadOnlyList<CurrencyRate> Currencies => _currencies;

    /// <summary>True when the token names a currency this table knows.</summary>
    public bool IsCurrency(string? token) => Resolve(token) is not null;

    /// <summary>Resolves a code, symbol, English or Chinese name to a currency.</summary>
    public CurrencyRate? Resolve(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var text = token.Trim();
        foreach (var currency in _currencies)
        {
            if (string.Equals(currency.Id, text, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(currency.Symbol, text, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(currency.Name, text, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(currency.NameZh, text, StringComparison.OrdinalIgnoreCase))
            {
                return currency;
            }
        }

        return CurrencyRates.Find(text);
    }

    /// <summary>Manual rate of a currency, in units per US dollar.</summary>
    public double? GetManualRate(string? code)
    {
        var currency = Resolve(code);
        return currency is not null && _manual.TryGetValue(currency.Id, out var value) ? value : null;
    }

    /// <summary>Edits a manual rate. Rejected unless the currency is known and the value is sane.</summary>
    public bool SetManualRate(string? code, double unitsPerUsd)
    {
        var currency = Resolve(code);
        if (currency is null || !double.IsFinite(unitsPerUsd) || unitsPerUsd <= 0 || unitsPerUsd > 1e9)
        {
            return false;
        }

        _manual[currency.Id] = unitsPerUsd;
        return true;
    }

    /// <summary>Restores the shipped default rates.</summary>
    public void ResetManualRates()
    {
        foreach (var currency in _currencies)
        {
            _manual[currency.Id] = currency.PerUsd;
        }
    }

    /// <summary>Switches back to the editable offline table.</summary>
    public void UseManualRates()
    {
        Source = CurrencyRateSource.Manual;
        Error = null;
    }

    /// <summary>
    /// Switches to daily rates. Nothing is fetched here; until <see cref="RefreshAsync"/> succeeds the
    /// table reports <see cref="UnavailableLabel"/> and refuses to convert.
    /// </summary>
    public void UseDailyRates()
    {
        Source = CurrencyRateSource.Daily;
        FetchedAt = null;
        Error = null;
        _live.Clear();
    }

    /// <summary>Fetches daily rates. Failure is recorded and leaves the table unavailable.</summary>
    public async ValueTask<bool> RefreshAsync(ICurrencyRateProvider provider, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (Source != CurrencyRateSource.Daily)
        {
            return false;
        }

        try
        {
            var quotes = await provider.GetRatesAsync(ct).ConfigureAwait(false);
            var usable = new List<CurrencyQuote>();
            foreach (var quote in quotes)
            {
                if (Resolve(quote.Code) is not null && double.IsFinite(quote.PerUsd) && quote.PerUsd > 0)
                {
                    usable.Add(quote);
                }
            }

            if (usable.Count == 0)
            {
                Fail(UnavailableLabel);
                return false;
            }

            _live.Clear();
            _live.AddRange(usable);
            ProviderName = provider.Name;
            FetchedAt = DateTimeOffset.Now;
            Error = null;
            return true;
        }
        catch (OperationCanceledException)
        {
            Fail(UnavailableLabel);
            return false;
        }
        catch (Exception ex)
        {
            Fail($"{UnavailableLabel} ({ex.GetType().Name})");
            return false;
        }
    }

    /// <summary>
    /// Converts between two currencies. Returns false with a reason when the active source cannot
    /// produce an honest answer.
    /// </summary>
    public bool TryConvert(double value, string? from, string? to, out double result, out string? error)
    {
        result = 0;
        error = null;
        var source = Resolve(from);
        var target = Resolve(to);
        if (source is null || target is null)
        {
            error = "未知货币 / unknown currency";
            return false;
        }

        if (!double.IsFinite(value))
        {
            error = "数值无效 / invalid value";
            return false;
        }

        if (!TryRate(source.Id, out var sourceRate) || !TryRate(target.Id, out var targetRate))
        {
            error = UnavailableLabel;
            return false;
        }

        if (sourceRate <= 0 || targetRate <= 0)
        {
            error = "汇率无效 / invalid rate";
            return false;
        }

        result = value / sourceRate * targetRate;
        return true;
    }

    /// <summary>Writes the manual table and the selected source into a plugin settings partition.</summary>
    public void SaveToSettings(IPluginSettings settings)
    {
        if (settings is null)
        {
            return;
        }

        try
        {
            var json = new JsonObject();
            foreach (var pair in _manual)
            {
                json[pair.Key] = pair.Value;
            }

            settings.Set(ManualSettingsKey, json.ToJsonString());
            settings.Set(SourceSettingsKey, Source.ToString());
        }
        catch
        {
            // Settings are best effort; the in-memory table keeps working.
        }
    }

    /// <summary>Reads manual rates back from a plugin settings partition.</summary>
    public void LoadFromSettings(IPluginSettings? settings)
    {
        if (settings is null)
        {
            return;
        }

        try
        {
            if (settings.Get<string?>(ManualSettingsKey) is { Length: > 0 } json &&
                JsonNode.Parse(json) is JsonObject parsed)
            {
                foreach (var pair in parsed)
                {
                    if (double.TryParse(pair.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
                    {
                        SetManualRate(pair.Key, rate);
                    }
                }
            }

            if (Enum.TryParse<CurrencyRateSource>(settings.Get<string?>(SourceSettingsKey), true, out var source))
            {
                if (source == CurrencyRateSource.Daily)
                {
                    UseDailyRates();
                }
                else
                {
                    UseManualRates();
                }
            }
        }
        catch
        {
            // Corrupt settings fall back to the shipped defaults.
        }
    }

    /// <summary>Snapshot of the table for the widget state, so a reload keeps the user's edits.</summary>
    public void Save(JsonObject target)
    {
        var manual = new JsonObject();
        foreach (var pair in _manual)
        {
            manual[pair.Key] = pair.Value;
        }

        target["manual"] = manual;
        target["source"] = Source.ToString();
        target["label"] = SourceLabel;
    }

    /// <summary>Restores manual rates stored in a widget state object.</summary>
    public void Load(JsonObject? source)
    {
        if (source is null)
        {
            return;
        }

        if (source["manual"] is JsonObject manual)
        {
            foreach (var pair in manual)
            {
                if (double.TryParse(pair.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
                {
                    SetManualRate(pair.Key, rate);
                }
            }
        }

        if (Enum.TryParse<CurrencyRateSource>(source["source"]?.ToString(), true, out var parsed))
        {
            if (parsed == CurrencyRateSource.Daily)
            {
                UseDailyRates();
            }
        }
    }

    /// <summary>
    /// The currency category as the unit picker shows it. The factors are for display only; every
    /// conversion goes through <see cref="TryConvert"/> so the source rules cannot be bypassed.
    /// </summary>
    public UnitCategory ToCategory()
    {
        var units = new List<UnitInfo>(_currencies.Count);
        foreach (var currency in _currencies)
        {
            var rate = TryRate(currency.Id, out var value) ? value : currency.PerUsd;
            units.Add(new UnitInfo(currency.Id, currency.Name, currency.NameZh, currency.Id, rate, 0, string.Equals(currency.Id, "USD", StringComparison.OrdinalIgnoreCase)));
        }

        return new UnitCategory(CategoryId, "Currency", "货币", units);
    }

    private bool TryRate(string code, out double rate)
    {
        rate = 0;
        if (Source == CurrencyRateSource.Daily)
        {
            if (!IsLive)
            {
                return false;
            }

            foreach (var quote in _live)
            {
                if (string.Equals(quote.Code, code, StringComparison.OrdinalIgnoreCase))
                {
                    rate = quote.PerUsd;
                    return true;
                }
            }

            // A currency missing from today's table is not guessed.
            return false;
        }

        return _manual.TryGetValue(code, out rate);
    }

    private void Fail(string message)
    {
        _live.Clear();
        FetchedAt = null;
        ProviderName = null;
        Error = message;
    }
}

/// <summary>
/// Daily rates from a public, keyless JSON endpoint. Constructed only after the user enabled daily
/// rates and the plugin holds the Network permission.
/// </summary>
public sealed class HttpCurrencyRateProvider : ICurrencyRateProvider, IDisposable
{
    private const string DefaultEndpoint = "https://open.er-api.com/v6/latest/USD";

    private readonly HttpClient _client;
    private readonly string _endpoint;
    private readonly bool _ownsClient;

    /// <summary>Creates a provider over an optional shared HTTP client.</summary>
    public HttpCurrencyRateProvider(HttpClient? client = null, string? endpoint = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint;
    }

    /// <inheritdoc />
    public string Name => "open.er-api.com";

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CurrencyQuote>> GetRatesAsync(CancellationToken ct = default)
    {
        using var response = await _client.GetAsync(_endpoint, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var node = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("unexpected rate payload");
        if (node["rates"] is not JsonObject rates)
        {
            throw new InvalidOperationException("rate payload has no rates object");
        }

        var quotes = new List<CurrencyQuote>(rates.Count);
        foreach (var pair in rates)
        {
            if (double.TryParse(pair.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var perUsd))
            {
                quotes.Add(new CurrencyQuote(pair.Key, perUsd));
            }
        }

        return quotes;
    }

    /// <summary>Disposes the HTTP client when this provider created it.</summary>
    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}

/// <summary>Rate provider backed by a delegate; used by panels and by the verification harness.</summary>
public sealed class DelegateCurrencyRateProvider : ICurrencyRateProvider
{
    private readonly Func<CancellationToken, ValueTask<IReadOnlyList<CurrencyQuote>>> _fetch;

    /// <summary>Creates a provider from a delegate.</summary>
    public DelegateCurrencyRateProvider(string name, Func<CancellationToken, ValueTask<IReadOnlyList<CurrencyQuote>>> fetch)
    {
        Name = name ?? "delegate";
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CurrencyQuote>> GetRatesAsync(CancellationToken ct = default) => _fetch(ct);
}

/// <summary>
/// Routes a conversion to the right engine: currency pairs go through the rate table (so the source
/// rules are enforced), everything else through the shared <see cref="IUnitConverter"/>.
/// </summary>
public static class CalculatorConversion
{
    /// <summary>Converts a value, choosing between the currency table and the unit converter.</summary>
    public static UnitConversionResult Convert(
        IUnitConverter converter,
        CurrencyRateTable rates,
        double value,
        string? fromUnit,
        string? toUnit)
    {
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(rates);

        if (rates.IsCurrency(fromUnit) || rates.IsCurrency(toUnit))
        {
            if (!rates.TryConvert(value, fromUnit, toUnit, out var converted, out var error))
            {
                return UnitConversionResult.Failure(error ?? CurrencyRateTable.UnavailableLabel);
            }

            var symbol = rates.Resolve(toUnit);
            return new UnitConversionResult(true, NumberFormatter.Format(converted, 12), null, fromUnit, symbol?.Id ?? toUnit);
        }

        return converter.Convert(value, fromUnit ?? string.Empty, toUnit ?? string.Empty);
    }

    /// <summary>
    /// All categories the converter offers: the engine catalogue plus this table's currency category.
    /// A currency category the engine may already expose is replaced, so the numbers the picker shows
    /// always come from the source this table is honest about.
    /// </summary>
    public static IReadOnlyList<UnitCategory> CategoriesWithCurrency(IUnitConverter converter, CurrencyRateTable rates)
    {
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(rates);
        var categories = new List<UnitCategory>(converter.Categories.Count + 1);
        foreach (var category in converter.Categories)
        {
            if (!string.Equals(category.Id, CurrencyRateTable.CategoryId, StringComparison.OrdinalIgnoreCase))
            {
                categories.Add(category);
            }
        }

        categories.Add(rates.ToCategory());
        return categories;
    }
}
