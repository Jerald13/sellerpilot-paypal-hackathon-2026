namespace SellerPilot.Api.PayPal.Models;

// Transaction Search: GET /v1/reporting/transactions (max 31-day window; data can lag by hours).

public sealed record TransactionSearchQuery(DateTimeOffset Start, DateTimeOffset End, string? TransactionId = null, int Page = 1, int PageSize = 100);

public sealed record PayPalTransactionSearchResponse(
    IReadOnlyList<PayPalTransactionDetail>? TransactionDetails,
    string? StartDate,
    string? EndDate,
    string? LastRefreshedDatetime,
    int? Page,
    int? TotalItems,
    int? TotalPages);

public sealed record PayPalTransactionDetail(PayPalTransactionInfo? TransactionInfo, PayPalPayerInfo? PayerInfo, PayPalCartInfo? CartInfo);

public sealed record PayPalTransactionInfo(
    string? TransactionId,
    string? PaypalReferenceId,
    string? TransactionEventCode,
    string? TransactionInitiationDate,
    string? TransactionUpdatedDate,
    PayPalMoney? TransactionAmount,
    PayPalMoney? FeeAmount,
    string? TransactionStatus,
    string? TransactionSubject,
    string? TransactionNote,
    string? InvoiceId,
    string? CustomField);

public sealed record PayPalPayerInfo(string? AccountId, string? EmailAddress, PayPalPayerName? PayerName, string? CountryCode);

public sealed record PayPalPayerName(string? GivenName, string? Surname, string? AlternateFullName)
{
    public string? DisplayName =>
        !string.IsNullOrWhiteSpace(AlternateFullName)
            ? AlternateFullName
            : string.Join(' ', new[] { GivenName, Surname }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } name ? name : null;
}

public sealed record PayPalCartInfo(IReadOnlyList<PayPalCartItem>? ItemDetails);

public sealed record PayPalCartItem(string? ItemName, string? ItemDescription, string? ItemQuantity);
