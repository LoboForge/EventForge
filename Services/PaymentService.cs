using EventForge.Configuration;
using EventForge.Payments;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text.Json;

namespace EventForge.Services;

public sealed record PublicPlan(
    string Id,
    string Name,
    string Description,
    decimal PriceUsd,
    long Credits,
    IReadOnlyList<string> Features);

public sealed record CheckoutResult(string CheckoutId, string RedirectUrl);
public sealed record PaymentInstructionsResult(string Method, string Json);

public sealed class ProviderNotConfiguredException : Exception
{
    public ProviderNotConfiguredException() : base("Payment provider is not configured.")
    {
    }
}

public sealed class PaymentService
{
    private readonly AccountStore _accounts;
    private readonly IPayPalPaymentClient _payPal;
    private readonly INowPaymentsClient _nowPayments;
    private readonly EventForgeOptions _options;
    private readonly ILogger<PaymentService> _log;

    public PaymentService(
        AccountStore accounts,
        IPayPalPaymentClient payPal,
        INowPaymentsClient nowPayments,
        IOptions<EventForgeOptions> options,
        ILogger<PaymentService> log)
    {
        _accounts = accounts;
        _payPal = payPal;
        _nowPayments = nowPayments;
        _options = options.Value;
        _log = log;
    }

    public IReadOnlyList<PublicPlan> GetPlans()
    {
        var configured = _options.Payments.Plans;
        return
        [
            ToPlan("starter", configured.Starter),
            ToPlan("pro", configured.Pro),
            ToPlan("scale", configured.Scale),
        ];
    }

    public PublicPlan? FindPlan(string planId) =>
        GetPlans().FirstOrDefault(p => string.Equals(p.Id, planId, StringComparison.OrdinalIgnoreCase));

    public async Task<CheckoutResult> CreateCheckoutAsync(
        string accountId,
        PublicPlan plan,
        string provider,
        CancellationToken ct)
    {
        var normalizedProvider = provider.Trim().ToLowerInvariant();
        if ((normalizedProvider == "paypal" && !_payPal.IsConfigured)
            || (normalizedProvider == "nowpayments" && !_nowPayments.IsConfigured))
        {
            throw new ProviderNotConfiguredException();
        }

        var checkoutId = Guid.NewGuid().ToString("N");
        await _accounts.CreatePurchaseAsync(
            new PurchaseRecord(
                checkoutId,
                accountId,
                plan.Id,
                normalizedProvider,
                null,
                plan.Credits,
                plan.PriceUsd,
                "pending",
                DateTimeOffset.UtcNow,
                null),
            ct);

        var publicUrl = _options.PublicUrl.TrimEnd('/');
        ProviderCheckout checkout;
        try
        {
            checkout = normalizedProvider switch
            {
                "paypal" => await _payPal.CreateOrderAsync(
                    checkoutId,
                    plan.PriceUsd,
                    $"{publicUrl}/checkout/paypal/return?checkout_id={Uri.EscapeDataString(checkoutId)}",
                    $"{publicUrl}/checkout?cancelled=1",
                    ct),
                "nowpayments" => await _nowPayments.CreateInvoiceAsync(
                    checkoutId,
                    plan.PriceUsd,
                    $"{publicUrl}/v1/public/webhooks/nowpayments",
                    $"{publicUrl}/checkout/success?checkout_id={Uri.EscapeDataString(checkoutId)}",
                    $"{publicUrl}/checkout?cancelled=1",
                    ct),
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            };
        }
        catch (PaymentProviderException ex)
        {
            _log.LogWarning(ex, "Payment checkout creation failed for provider {Provider}", normalizedProvider);
            throw;
        }

        await _accounts.SetPurchaseProviderOrderAsync(checkoutId, checkout.ProviderOrderId, ct);
        return new CheckoutResult(checkoutId, checkout.RedirectUrl);
    }

    public async Task<PurchaseCompletion?> CapturePayPalAsync(
        string checkoutId,
        string accountId,
        CancellationToken ct)
    {
        if (!_payPal.IsConfigured) throw new ProviderNotConfiguredException();
        var purchase = await _accounts.GetPurchaseAsync(checkoutId, ct);
        if (purchase == null
            || !string.Equals(purchase.AccountId, accountId, StringComparison.Ordinal)
            || !string.Equals(purchase.Provider, "paypal", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(purchase.ProviderOrderId))
        {
            return null;
        }
        if (!string.Equals(purchase.Status, "completed", StringComparison.Ordinal))
            await _payPal.CaptureOrderAsync(purchase.ProviderOrderId, ct);
        return await _accounts.CompletePurchaseAsync(
            checkoutId,
            accountId,
            AccountService.CreateApiKey,
            ct);
    }

    public async Task<PurchaseCompletion?> CompleteNowPaymentsAsync(
        string checkoutId,
        CancellationToken ct)
    {
        var purchase = await _accounts.GetPurchaseAsync(checkoutId, ct);
        if (purchase == null
            || !string.Equals(purchase.Provider, "nowpayments", StringComparison.Ordinal))
        {
            return null;
        }
        return await _accounts.CompletePurchaseAsync(
            checkoutId,
            expectedAccountId: null,
            AccountService.CreateApiKey,
            ct);
    }

    public async Task<PaymentInstructionsResult> CreateCapacityPaymentInstructionsAsync(
        CapacityRequestRecord request,
        string method,
        string? invoiceUrl,
        decimal? amountHintUsd,
        string? note,
        CancellationToken ct)
    {
        var normalized = method.Trim().ToLowerInvariant();
        object payload = normalized switch
        {
            "paypal" => await BuildPayPalInvoiceAsync(request, invoiceUrl, amountHintUsd, note, ct),
            "wire" => BuildWireInstructions(request, amountHintUsd),
            "monero" => BuildMoneroInstructions(request, amountHintUsd),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };
        return new PaymentInstructionsResult(normalized, JsonSerializer.Serialize(payload));
    }

    private async Task<object> BuildPayPalInvoiceAsync(
        CapacityRequestRecord request,
        string? invoiceUrl,
        decimal? amountHintUsd,
        string? note,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(invoiceUrl))
        {
            return new
            {
                method = "paypal",
                invoice_url = invoiceUrl.Trim(),
                invoice_id = (string?)null,
                amount_hint_usd = amountHintUsd,
                note,
                manually_attached = true,
            };
        }
        if (!_payPal.IsConfigured || amountHintUsd is null or <= 0)
        {
            return new
            {
                method = "paypal",
                recipient_email = request.Email,
                amount_hint_usd = amountHintUsd,
                request_id = request.RequestId,
                note,
                manual_invoice_required = true,
                message = "Create a PayPal invoice manually, then attach its URL with this endpoint.",
            };
        }
        var invoice = await _payPal.CreateInvoiceAsync(request.RequestId, request.Email, amountHintUsd.Value, ct);
        return new
        {
            method = "paypal",
            invoice_url = invoice.InvoiceUrl,
            invoice_id = invoice.InvoiceId,
            amount_hint_usd = amountHintUsd,
            note,
            manually_attached = false,
        };
    }

    private object BuildWireInstructions(CapacityRequestRecord request, decimal? amountHintUsd)
    {
        var wire = _options.Payments.Wire;
        return new
        {
            method = "wire",
            bank_name = wire.BankName,
            account_name = wire.AccountName,
            account_number = wire.AccountNumber,
            routing_number = wire.RoutingNumber,
            swift = wire.Swift,
            iban = wire.Iban,
            reference = wire.ReferenceTemplate.Replace("{request_id}", request.RequestId, StringComparison.OrdinalIgnoreCase),
            notes = wire.Notes,
            amount_hint_usd = amountHintUsd,
        };
    }

    private object BuildMoneroInstructions(CapacityRequestRecord request, decimal? amountHintUsd)
    {
        var monero = _options.Payments.Monero;
        if (!monero.Enabled || string.IsNullOrWhiteSpace(monero.ReceiveAddress))
            throw new ProviderNotConfiguredException();
        var orderRef = $"EFXMR-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";
        return new
        {
            method = "monero",
            address = monero.ReceiveAddress,
            payment_id_or_memo = orderRef,
            amount_hint_usd = amountHintUsd,
            request_id = request.RequestId,
            matching_note = "Include the order reference when contacting ops. This v1 uses one receive address and a unique order reference; wallet-RPC subaddresses are not yet enabled.",
        };
    }

    private static PublicPlan ToPlan(string id, PlanOptions plan) =>
        new(id, plan.Name, plan.Description, plan.PriceUsd, plan.Credits, plan.Features);
}
