using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SellerPilot.Api.PayPal.Models;

public static class PayPalJson
{
    /// <summary>PayPal REST payloads are snake_case; nulls are omitted on write.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>PayPal sends money as a decimal string ("45.00"); it is converted to decimal at the edge, never to double.</summary>
public sealed record PayPalMoney(string? CurrencyCode, string? Value)
{
    public bool TryGetAmount(out decimal amount) =>
        decimal.TryParse(Value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);

    public static PayPalMoney From(decimal amount, string currency) => new(currency, Money.Format(amount, currency));
}

public sealed record PayPalLink(string? Href, string? Rel, string? Method);

public sealed record PayPalName(string? GivenName, string? Surname)
{
    public string? FullName =>
        string.Join(' ', new[] { GivenName, Surname }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } name ? name : null;
}

public sealed record PayPalAddress(string? CountryCode);

public sealed record PayPalStatusDetails(string? Reason);

public static class Money
{
    private static readonly HashSet<string> ZeroDecimalCurrencies = new(StringComparer.OrdinalIgnoreCase) { "JPY", "HUF", "TWD" };

    public static int Decimals(string currency) => ZeroDecimalCurrencies.Contains(currency) ? 0 : 2;

    public static string Format(decimal amount, string currency) =>
        decimal.Round(amount, Decimals(currency), MidpointRounding.AwayFromZero)
            .ToString(Decimals(currency) == 0 ? "0" : "0.00", CultureInfo.InvariantCulture);

    public static string Display(decimal amount, string currency) => $"{currency} {Format(amount, currency)}";

    public static bool HasValidPrecision(decimal amount, string currency) => decimal.Round(amount, Decimals(currency)) == amount;

    public static bool IsCurrencyCode(string? code) => code is { Length: 3 } && code.All(char.IsAsciiLetterUpper);
}

/// <summary>PayPal mostly sends RFC 3339 dates, but Transaction Search uses offsets like "+0000".</summary>
public static class PayPalDates
{
    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        // "2026-10-31T10:00:00+0000" -> "2026-10-31T10:00:00+00:00"
        if (text.Length > 5 && (text[^5] == '+' || text[^5] == '-') && text[^4..].All(char.IsAsciiDigit))
        {
            text = text[..^2] + ":" + text[^2..];
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Invoice dates are plain "yyyy-MM-dd".</summary>
    public static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
