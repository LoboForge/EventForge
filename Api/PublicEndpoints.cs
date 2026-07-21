using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EventForge.Configuration;
using EventForge.Payments;
using EventForge.Services;
using Microsoft.Extensions.Options;

namespace EventForge.Api;

public static class PublicEndpoints
{
    private static readonly object[] Models =
    [
        Model("wan2t2v", "Wan 2.2 14B text-to-video", "video", "Text-to-video generation with Wan 2.2 14B.", true),
        Model("wan2", "Wan 2.2 14B image-to-video", "video", "Image-to-video generation with Wan 2.2 14B.", true),
        Model("flux2klein", "FLUX.2 Klein image gen", "image", "Fast, high-quality FLUX.2 Klein image generation.", true),
        Model("flux2klein-edit", "FLUX.2 Klein Edit", "image editing", "Instruction-based image editing with FLUX.2 Klein.", true),
        Model("zimage", "Z-Image Turbo", "image", "Fast image generation with Z-Image Turbo.", true),
        Model("chroma", "Chroma HD", "image", "High-definition image generation with Chroma.", true),
        Model("ltx23", "LTX-2 video", "video", "Video generation with LTX-2.", false),
        Model("music", "ACE-Step music generation", "audio", "Music and audio generation with ACE-Step.", false),
        Model("joycaption", "JoyCaption image captioning", "caption", "Detailed text captioning for images.", false),
        Model("dolphin", "Dolphin/LLM text generation", "text", "General-purpose text generation.", false),
    ];

    public static void MapPublicEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/public/plans", (PaymentService payments) =>
            Results.Ok(new
            {
                plans = payments.GetPlans().Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    description = p.Description,
                    price_usd = p.PriceUsd,
                    credits = p.Credits,
                    features = p.Features,
                    starting_at = true,
                }),
                guidance = "Starting package estimates; submit a capacity request for confirmation.",
                custom = new { enterprise_contact = "sales@loboforge.com" },
            }));

        app.MapGet("/v1/public/models", () => Results.Ok(new { models = Models }));

        app.MapPost("/v1/public/capacity-request", async (
            HttpContext ctx,
            CapacityRequestBody body,
            AccountService accounts,
            CancellationToken ct) =>
        {
            if (!IsValidEmail(body.Email)
                || body.Models == null
                || body.Models.Count == 0
                || body.Models.Count > 25
                || body.Models.Any(m => string.IsNullOrWhiteSpace(m) || m.Length > 100)
                || body.EstimatedJobs <= 0
                || body.EstimatedJobs > 1_000_000_000
                || body.Email!.Length > 320
                || body.Company?.Length > 200
                || body.Name?.Length > 200
                || body.Notes?.Length > 5_000)
            {
                return Results.BadRequest(new { error = "invalid_request" });
            }
            var preferred = body.PreferredPayment?.Trim().ToLowerInvariant();
            if (preferred is not ("paypal" or "wire" or "monero" or "any"))
                return Results.BadRequest(new { error = "invalid_preferred_payment" });

            var account = await AuthenticateAsync(ctx, accounts, ct);
            if (account != null
                && !string.Equals(account.Email, body.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "session_email_mismatch" });
            }
            var request = await accounts.CreateCapacityRequestAsync(
                account?.AccountId,
                body.Email,
                body.Company,
                body.Name,
                body.Models,
                body.EstimatedJobs,
                body.Notes,
                preferred,
                ct);
            return Results.Ok(new
            {
                request_id = request.RequestId,
                status = "received",
                message = "Capacity request received. We will email payment instructions for a PayPal invoice, wire transfer, or Monero payment.",
            });
        });

        app.MapPost("/v1/public/register", async (
            RegisterRequest body,
            AccountService accounts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!IsValidEmail(body.Email))
                return Results.BadRequest(new { error = "invalid_email" });
            if (body.Password == null || body.Password.Length < 8)
                return Results.BadRequest(new { error = "invalid_password" });
            if (body.Password.Length > 1024 || body.Email!.Length > 320 || body.Company?.Length > 200)
                return Results.BadRequest(new { error = "invalid_request" });
            try
            {
                var account = await accounts.RegisterAsync(body.Email!, body.Password, body.Company, ct);
                return account == null
                    ? Results.Conflict(new { error = "email_exists" })
                    : Results.Ok(new
                    {
                        account_id = account.AccountId,
                        email = account.Email,
                        created_at = account.CreatedAt,
                    });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loggerFactory.CreateLogger("PublicAccounts").LogError(ex, "Account registration failed");
                return Results.Json(
                    new { error = "internal_error" },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPost("/v1/public/login", async (
            LoginRequest body,
            AccountService accounts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Email)
                || body.Password == null
                || body.Email.Length > 320
                || body.Password.Length > 1024)
            {
                return Results.Unauthorized();
            }
            try
            {
                var login = await accounts.LoginAsync(body.Email, body.Password, ct);
                return login == null
                    ? Results.Unauthorized()
                    : Results.Ok(new
                    {
                        session_token = login.SessionToken,
                        account_id = login.Account.AccountId,
                        email = login.Account.Email,
                    });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loggerFactory.CreateLogger("PublicAccounts").LogError(ex, "Account login failed");
                return Results.Json(
                    new { error = "internal_error" },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapGet("/v1/public/account", async (
            HttpContext ctx,
            AccountService accounts,
            CancellationToken ct) =>
        {
            var account = await AuthenticateAsync(ctx, accounts, ct);
            return account == null
                ? Results.Unauthorized()
                : Results.Ok(new
                {
                    account_id = account.AccountId,
                    email = account.Email,
                    company = account.Company,
                    credits = account.Credits,
                    api_key = account.ApiKey,
                    created_at = account.CreatedAt,
                });
        });

        app.MapPost("/v1/public/checkout", ManualBillingGone);
        app.MapPost("/v1/public/checkout/{checkoutId}/capture", ManualBillingGone);
        app.MapPost("/v1/public/webhooks/nowpayments", ManualBillingGone);
    }

    private static async Task<AccountRecord?> AuthenticateAsync(
        HttpContext context,
        AccountService accounts,
        CancellationToken ct)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        var token = authorization["Bearer ".Length..].Trim();
        return token.Length == 0 ? null : await accounts.AuthenticateSessionAsync(token, ct);
    }

    private static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        try
        {
            var parsed = new MailAddress(email.Trim());
            return string.Equals(parsed.Address, email.Trim(), StringComparison.OrdinalIgnoreCase)
                   && parsed.Host.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryValidateNowPaymentsSignature(
        string body,
        string signature,
        string secret,
        out JsonDocument payload)
    {
        payload = null!;
        try
        {
            payload = JsonDocument.Parse(body);
            var canonical = Canonicalize(payload.RootElement);
            using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
            var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
            var supplied = signature.Trim().ToLowerInvariant();
            if (expected.Length != supplied.Length
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected),
                    Encoding.ASCII.GetBytes(supplied)))
            {
                payload.Dispose();
                payload = null!;
                return false;
            }
            return true;
        }
        catch (JsonException)
        {
            payload?.Dispose();
            payload = null!;
            return false;
        }
    }

    private static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
            return;
        }
        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetInt64(out var integer))
                writer.WriteNumberValue(integer);
            else if (element.TryGetDecimal(out var decimalValue))
                writer.WriteNumberValue(decimalValue);
            else
                writer.WriteNumberValue(element.GetDouble());
            return;
        }
        element.WriteTo(writer);
    }

    private static IResult ProviderNotConfigured() =>
        Results.Json(
            new { error = "provider_not_configured" },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult ManualBillingGone() =>
        Results.Json(
            new
            {
                error = "manual_billing",
                message = "Automated checkout is unavailable. Submit POST /v1/public/capacity-request instead.",
            },
            statusCode: StatusCodes.Status410Gone);

    private static object Model(
        string id,
        string name,
        string kind,
        string description,
        bool supportsCustomLoras) =>
        new
        {
            id,
            name,
            kind,
            description,
            supports_custom_loras = supportsCustomLoras,
        };
}

public sealed record RegisterRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("company")] string? Company);

public sealed record LoginRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("password")] string? Password);

public sealed record CheckoutRequest(
    [property: JsonPropertyName("plan_id")] string? PlanId,
    [property: JsonPropertyName("provider")] string? Provider);

public sealed record CapacityRequestBody(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("company")] string? Company,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("models")] List<string>? Models,
    [property: JsonPropertyName("estimated_jobs")] long EstimatedJobs,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("preferred_payment")] string? PreferredPayment);
