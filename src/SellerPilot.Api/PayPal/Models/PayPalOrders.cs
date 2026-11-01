namespace SellerPilot.Api.PayPal.Models;

// Orders v2: GET /v2/checkout/orders/{id}. Only the fields SellerPilot uses are mapped.

public sealed record PayPalOrder(
    string Id,
    string? Status,
    string? Intent,
    IReadOnlyList<PayPalPurchaseUnit>? PurchaseUnits,
    PayPalPayer? Payer,
    PayPalPaymentSource? PaymentSource,
    string? CreateTime,
    string? UpdateTime,
    IReadOnlyList<PayPalLink>? Links);

public sealed record PayPalPurchaseUnit(
    string? ReferenceId,
    PayPalMoney? Amount,
    string? Description,
    string? CustomId,
    string? InvoiceId,
    string? SoftDescriptor,
    IReadOnlyList<PayPalItem>? Items,
    PayPalPaymentCollection? Payments);

public sealed record PayPalItem(string? Name, string? Quantity, PayPalMoney? UnitAmount, string? Description, string? Sku);

public sealed record PayPalPaymentCollection(IReadOnlyList<PayPalCapture>? Captures, IReadOnlyList<PayPalRefund>? Refunds);

public sealed record PayPalPayer(PayPalName? Name, string? EmailAddress, string? PayerId, PayPalAddress? Address);

public sealed record PayPalPaymentSource(PayPalWalletSource? Paypal, PayPalCardSource? Card);

public sealed record PayPalWalletSource(PayPalName? Name, string? EmailAddress, string? AccountId, PayPalAddress? Address);

public sealed record PayPalCardSource(string? Name, string? LastDigits, string? Brand);
