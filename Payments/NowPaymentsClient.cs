using System.Net.Http.Json;
using System.Text.Json;
using EventForge.Configuration;
using Microsoft.Extensions.Options;

namespace EventForge.Payments;

public interface INowPaymentsClient
{
    bool IsConfigured { get; }
    Task<ProviderCheckout> CreateInvoiceAsync(
        string checkoutId,
        decimal priceUsd,
        string ipnCallbackUrl,
        string successUrl,
        string cancelUrl,
        CancellationToken ct);
}

public sealed class NowPaymentsClient : INowPaymentsClient
{
    private const string ApiBase = "https://api.nowpayments.io/v1";
    private readonly HttpClient _http;
    private readonly NowPaymentsOptions _options;

    public NowPaymentsClient(HttpClient http, IOptions<EventForgeOptions> options)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(30);
        _options = options.Value.Payments.NowPayments;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.ApiKey)
        && !string.IsNullOrWhiteSpace(_options.IpnSecret);

    public async Task<ProviderCheckout> CreateInvoiceAsync(
        string checkoutId,
        decimal priceUsd,
        string ipnCallbackUrl,
        string successUrl,
        string cancelUrl,
        CancellationToken ct)
    {
        if (!IsConfigured) throw new PaymentProviderException("NOWPayments is not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/invoice")
        {
            Content = JsonContent.Create(new
            {
                price_amount = priceUsd,
                price_currency = "usd",
                order_id = checkoutId,
                order_description = $"EventForge credits ({checkoutId})",
                ipn_callback_url = ipnCallbackUrl,
                success_url = successUrl,
                cancel_url = cancelUrl,
            }),
        };
        request.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new PaymentProviderException($"NOWPayments invoice creation failed with HTTP {(int)response.StatusCode}.");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        var invoiceId = root.TryGetProperty("id", out var id)
            ? id.ToString()
            : root.TryGetProperty("invoice_id", out var invoice) ? invoice.ToString() : null;
        var invoiceUrl = root.TryGetProperty("invoice_url", out var url) ? url.GetString() : null;
        if (string.IsNullOrWhiteSpace(invoiceId) || string.IsNullOrWhiteSpace(invoiceUrl))
            throw new PaymentProviderException("NOWPayments returned an incomplete invoice response.");
        return new ProviderCheckout(invoiceId, invoiceUrl);
    }
}
