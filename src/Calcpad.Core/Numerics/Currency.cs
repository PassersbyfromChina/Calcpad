namespace Calcpad.Core.Numerics;

/// <summary>One currency with its offline conversion rate.</summary>
/// <param name="Id">ISO style code, for example <c>USD</c>.</param>
/// <param name="Name">English name, plural friendly so <c>dollars</c> resolves.</param>
/// <param name="NameZh">Chinese name.</param>
/// <param name="Symbol">Symbol, written before the number.</param>
/// <param name="PerUsd">How many units of this currency one US dollar buys.</param>
public sealed record CurrencyRate(string Id, string Name, string NameZh, string Symbol, double PerUsd);

/// <summary>
/// Currency table used by Math Notes variables and conversions such as <c>taxi = $27</c> or
/// <c>$100 in yen</c>. The rates are the offline defaults that ship with the app, so no network
/// access is needed; every conversion is deterministic and testable.
/// </summary>
public static class CurrencyRates
{
    /// <summary>Symbols understood directly in front of a number.</summary>
    public static IReadOnlyList<char> Prefixes { get; } = ['$', '€', '£', '¥', '₩', '₹'];

    /// <summary>Human readable note about where the rates come from.</summary>
    public const string Note = "offline default rates (units per US dollar)";

    /// <summary>Every known currency, US dollar first.</summary>
    public static IReadOnlyList<CurrencyRate> All { get; } =
    [
        new("USD", "Dollar", "美元", "$", 1),
        new("EUR", "Euro", "欧元", "€", 0.92),
        new("GBP", "Pound sterling", "英镑", "£", 0.79),
        new("JPY", "Yen", "日元", "¥", 157),
        new("CNY", "Yuan", "人民币", "元", 7.25),
        new("KRW", "Won", "韩元", "₩", 1380),
        new("INR", "Rupee", "印度卢比", "₹", 83.5),
        new("CAD", "Canadian dollar", "加元", "C$", 1.37),
        new("AUD", "Australian dollar", "澳元", "A$", 1.52),
        new("CHF", "Swiss franc", "瑞士法郎", "CHF", 0.89),
        new("HKD", "Hong Kong dollar", "港元", "HK$", 7.81),
        new("SGD", "Singapore dollar", "新加坡元", "S$", 1.35),
        new("TWD", "New Taiwan dollar", "新台币", "NT$", 32.3),
        new("THB", "Baht", "泰铢", "฿", 36.5),
        new("RUB", "Ruble", "卢布", "₽", 92),
        new("BRL", "Real", "雷亚尔", "R$", 5.4)
    ];

    /// <summary>Finds a currency by symbol, code, English name or Chinese name.</summary>
    public static CurrencyRate? Find(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var text = token.Trim();
        foreach (var rate in All)
        {
            if (string.Equals(rate.Symbol, text, StringComparison.OrdinalIgnoreCase)
                || string.Equals(rate.Id, text, StringComparison.OrdinalIgnoreCase)
                || string.Equals(rate.Name, text, StringComparison.OrdinalIgnoreCase)
                || string.Equals(rate.NameZh, text, StringComparison.OrdinalIgnoreCase))
            {
                return rate;
            }
        }

        // "dollars", "euros", "yen" all resolve through the same plural tolerance as other units.
        var singular = UnitCatalog.Singularize(text);
        if (!string.Equals(singular, text, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var rate in All)
            {
                if (string.Equals(rate.Name, singular, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(rate.Id, singular, StringComparison.OrdinalIgnoreCase))
                {
                    return rate;
                }
            }
        }

        return null;
    }

    /// <summary>True when the character can start a currency amount.</summary>
    public static bool IsPrefix(char c) => Prefixes.Contains(c);
}
