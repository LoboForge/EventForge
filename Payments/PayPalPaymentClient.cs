using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EventForge.Configuration;
using Microsoft.Extensions.Options;

namespace EventForge.Payments;

public sealed record ProviderCheckout(string ProviderOrderId, string RedirectUrl);
public sealed record PayPalInvoice(string InvoiceId, string InvoiceUrl);

public sealed class PaymentProviderException : Exception
{
    public PaymentProviderException(string message) : base(message)
    {
    }
}

public interface IPayPalPaymentClient
{
    bool IsConfigured { get; }
    Task<ProviderCheckout> CreateOrderAsync(
        string checkoutId,
        decimal priceUsd,
        string returnUrl,
        string cancelUrl,
        CancellationToken ct);
    Task CaptureOrderAsync(string providerOrderId, CancellationToken ct);
    Task<PayPalInvoice> CreateInvoiceAsync(
        string requestId,
        string recipientEmail,
        decimal amountUsd,
        CancellationToken ct);
}

public sealed class PayPalPaymentClient : IPayPalPaymentClient
{
    private readonly HttpClient _http;
    private readonly PayPalOptions _options;

    public PayPalPaymentClient(HttpClient http, IOptions<EventForgeOptions> options)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(30);
        _options = options.Value.Payments.PayPal;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.ClientId)
        && !string.IsNullOrWhiteSpace(_options.Secret);

    public async Task<ProviderCheckout> CreateOrderAsync(
        string checkoutId,
        decimal priceUsd,
        string returnUrl,
        string cancelUrl,
        CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/v2/checkout/orders")
        {
            Content = JsonContent.Create(new
            {
                intent = "CAPTURE",
                purchase_units = new[]
                {
                    new
                    {
                        reference_id = checkoutId,
                        custom_id = checkoutId,
                        amount = new
                        {
                            currency_code = "USD",
                            value = priceUsd.ToString("0.00", CultureInfo.InvariantCulture),
                        },
                    },
                },
                application_context = new
                {
                    return_url = returnUrl,
                    cancel_url = cancelUrl,
                    user_action = "PAY_NOW",
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("PayPal-Request-Id", checkoutId);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new PaymentProviderException($"PayPal order creation failed with HTTP {(int)response.StatusCode}.");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        var orderId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
        string? approveUrl = null;
        if (root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in links.EnumerateArray())
            {
                if (link.TryGetProperty("rel", out var rel)
                    && string.Equals(rel.GetString(), "approve", StringComparison.OrdinalIgnoreCase)
                    && link.TryGetProperty("href", out var href))
                {
                    approveUrl = href.GetString();
                    break;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(approveUrl))
            throw new PaymentProviderException("PayPal returned an incomplete order response.");
        return new ProviderCheckout(orderId, approveUrl);
    }

    public async Task CaptureOrderAsync(string providerOrderId, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{ApiBase}/v2/checkout/orders/{Uri.EscapeDataString(providerOrderId)}/capture")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new PaymentProviderException($"PayPal capture failed with HTTP {(int)response.StatusCode}.");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var status = document.RootElement.TryGetProperty("status", out var value) ? value.GetString() : null;
        if (!string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            throw new PaymentProviderException("PayPal did not report a completed capture.");
    }

    public async Task<PayPalInvoice> CreateInvoiceAsync(
        string requestId,
        string recipientEmail,
        decimal amountUsd,
        CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        using var create = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/v2/invoicing/invoices")
        {
            Content = JsonContent.Create(new
            {
                detail = new
                {
                    invoice_number = $"EF-{requestId[..Math.Min(16, requestId.Length)]}",
                    reference = requestId,
                    currency_code = "USD",
                    note = _options.InvoiceNote,
                    payment_term = new { term_type = "DUE_ON_RECEIPT" },
                },
                primary_recipients = new[]
                {
                    new { billing_info = new { email_address = recipientEmail } },
                },
                items = new[]
                {
                    new
                    {
                        name = "EventForge prepaid capacity",
                        quantity = "1",
                        unit_amount = new
                        {
                            currency_code = "USD",
                            value = amountUsd.ToString("0.00", CultureInfo.InvariantCulture),
                        },
                    },
                },
            }),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        create.Headers.TryAddWithoutValidation("PayPal-Request-Id", $"invoice-{requestId}");
        using var createResponse = await _http.SendAsync(create, ct);
        if (!createResponse.IsSuccessStatusCode)
            throw new PaymentProviderException($"PayPal invoice creation failed with HTTP {(int)createResponse.StatusCode}.");

        using var document = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync(ct));
        var href = document.RootElement.TryGetProperty("href", out var hrefValue) ? hrefValue.GetString() : null;
        var invoiceId = string.IsNullOrWhiteSpace(href)
            ? null
            : href.TrimEnd('/').Split('/').LastOrDefault();
        if (string.IsNullOrWhiteSpace(invoiceId))
            throw new PaymentProviderException("PayPal returned no invoice id.");

        using var send = new HttpRequestMessage(
            HttpMethod.Post,
            $"{ApiBase}/v2/invoicing/invoices/{Uri.EscapeDataString(invoiceId)}/send")
        {
            Content = JsonContent.Create(new { send_to_recipient = true }),
        };
        send.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var sendResponse = await _http.SendAsync(send, ct);
        if (!sendResponse.IsSuccessStatusCode)
            throw new PaymentProviderException($"PayPal invoice send failed with HTTP {(int)sendResponse.StatusCode}.");

        var webBase = string.Equals(_options.Mode, "live", StringComparison.OrdinalIgnoreCase)
            ? "https://www.paypal.com"
            : "https://www.sandbox.paypal.com";
        return new PayPalInvoice(invoiceId, $"{webBase}/invoice/p/#{Uri.EscapeDataString(invoiceId)}");
    }

    private string ApiBase =>
        string.Equals(_options.Mode, "live", StringComparison.OrdinalIgnoreCase)
            ? "https://api-m.paypal.com"
            : "https://api-m.sandbox.paypal.com";

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new PaymentProviderException("PayPal is not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/v1/oauth2/token")
        {
            Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.Secret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new PaymentProviderException($"PayPal authentication failed with HTTP {(int)response.StatusCode}.");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var token = document.RootElement.TryGetProperty("access_token", out var value) ? value.GetString() : null;
        return string.IsNullOrWhiteSpace(token)
            ? throw new PaymentProviderException("PayPal returned no access token.")
            : token;
    }
}
