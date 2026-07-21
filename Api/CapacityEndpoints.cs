using System.Text.Json;
using System.Text.Json.Serialization;
using EventForge.Auth;
using EventForge.Payments;
using EventForge.Services;

namespace EventForge.Api;

public static class CapacityEndpoints
{
    public static void MapCapacityEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/ops/capacity-requests", async (
            HttpContext ctx,
            AccountStore store,
            IOpsKeyValidator opsAuth,
            string? status,
            string? payment_method,
            string? q,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _)) return Results.Unauthorized();
            var rows = await store.ListCapacityRequestsAsync(
                status?.Contains(',') == true ? null : status,
                ct);
            var statuses = string.IsNullOrWhiteSpace(status)
                ? null
                : status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var filtered = rows
                .Where(r => statuses == null || statuses.Contains(r.Status))
                .Where(r => string.IsNullOrWhiteSpace(payment_method)
                            || string.Equals(r.PaymentMethod, payment_method, StringComparison.OrdinalIgnoreCase))
                .Where(r => string.IsNullOrWhiteSpace(q)
                            || r.Email.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || (r.Company?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                            || r.RequestId.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(ToDto)
                .ToList();
            return Results.Ok(new { count = filtered.Count, requests = filtered });
        });

        app.MapPost("/v1/ops/capacity-requests/{id}/payment-instructions", async (
            string id,
            HttpContext ctx,
            CapacityPaymentInstructionsBody body,
            AccountStore store,
            PaymentService payments,
            IOpsKeyValidator opsAuth,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _)) return Results.Unauthorized();
            var method = body.Method?.Trim().ToLowerInvariant();
            if (method is not ("paypal" or "wire" or "monero"))
                return Results.BadRequest(new { error = "invalid_payment_method" });
            if (body.AmountHintUsd is <= 0)
                return Results.BadRequest(new { error = "invalid_amount_hint" });
            var request = await store.GetCapacityRequestAsync(id, ct);
            if (request == null) return Results.NotFound(new { error = "capacity_request_not_found" });
            try
            {
                var instructions = await payments.CreateCapacityPaymentInstructionsAsync(
                    request,
                    method,
                    body.InvoiceUrl,
                    body.AmountHintUsd,
                    body.Note,
                    ct);
                var updated = await store.SetCapacityPaymentInstructionsAsync(id, method, instructions.Json, ct);
                if (updated == null) return Results.Conflict(new { error = "capacity_request_closed" });
                var payload = JsonSerializer.Deserialize<JsonElement>(instructions.Json);
                return Results.Ok(new
                {
                    request_id = id,
                    status = updated.Status,
                    method,
                    payment_instructions = payload,
                    invoice_url = ReadString(payload, "invoice_url"),
                    invoice_id = ReadString(payload, "invoice_id"),
                    address = ReadString(payload, "address"),
                    payment_id_or_memo = ReadString(payload, "payment_id_or_memo"),
                });
            }
            catch (ProviderNotConfiguredException)
            {
                return Results.Json(
                    new
                    {
                        error = "provider_not_configured",
                        method,
                        message = "Configure this payment method before generating instructions.",
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (PaymentProviderException ex)
            {
                loggerFactory.CreateLogger("CapacityPayments")
                    .LogWarning(ex, "Payment instruction provider call failed for {RequestId}", id);
                return Results.Json(
                    new { error = "payment_provider_error", message = ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/v1/ops/capacity-requests/{id}/approve", async (
            string id,
            HttpContext ctx,
            CapacityApproveBody? body,
            AccountService accounts,
            IOpsKeyValidator opsAuth,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _)) return Results.Unauthorized();
            var credits = body?.Credits ?? 0;
            if (credits < 0) return Results.BadRequest(new { error = "invalid_credits" });
            var result = await accounts.ApproveCapacityRequestAsync(id, credits, ct);
            if (result == null) return Results.NotFound(new { error = "capacity_request_not_found_or_rejected" });
            if (body?.ApiKeyEmail == true)
            {
                loggerFactory.CreateLogger("CapacityRequests")
                    .LogInformation("API key email requested for {RequestId}; email delivery is not configured", id);
            }
            return Results.Ok(new
            {
                request_id = id,
                status = "approved",
                account_id = result.AccountId,
                api_key = result.ApiKey,
                credits = result.Credits,
                account_created = result.AccountCreated,
                api_key_email_sent = false,
                capacity_note = body?.CapacityNote,
            });
        });

        app.MapPost("/v1/ops/capacity-requests/{id}/reject", async (
            string id,
            HttpContext ctx,
            CapacityRejectBody body,
            AccountStore store,
            IOpsKeyValidator opsAuth,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Length > 2_000)
                return Results.BadRequest(new { error = "reason_required" });
            var request = await store.RejectCapacityRequestAsync(id, body.Reason.Trim(), ct);
            return request == null
                ? Results.NotFound(new { error = "capacity_request_not_found_or_approved" })
                : Results.Ok(new { request_id = id, status = "rejected", reason = request.RejectionReason });
        });
    }

    private static object ToDto(CapacityRequestRecord r) => new
    {
        request_id = r.RequestId,
        account_id = r.AccountId,
        email = r.Email,
        company = r.Company,
        name = r.Name,
        models = JsonSerializer.Deserialize<string[]>(r.ModelsJson) ?? [],
        estimated_jobs = r.EstimatedJobs,
        notes = r.Notes,
        preferred_payment = r.PreferredPayment,
        status = r.Status,
        payment_method = r.PaymentMethod,
        payment_instructions = string.IsNullOrWhiteSpace(r.PaymentInstructionsJson)
            ? (JsonElement?)null
            : JsonSerializer.Deserialize<JsonElement>(r.PaymentInstructionsJson),
        rejection_reason = r.RejectionReason,
        credits_granted = r.CreditsGranted,
        created_at = r.CreatedAt,
        updated_at = r.UpdatedAt,
    };

    private static string? ReadString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var found)
        && found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;
}

public sealed record CapacityPaymentInstructionsBody(
    [property: JsonPropertyName("method")] string? Method,
    [property: JsonPropertyName("invoice_url")] string? InvoiceUrl,
    [property: JsonPropertyName("amount_hint_usd")] decimal? AmountHintUsd,
    [property: JsonPropertyName("note")] string? Note);

public sealed record CapacityApproveBody(
    [property: JsonPropertyName("credits")] long? Credits,
    [property: JsonPropertyName("api_key_email")] bool ApiKeyEmail,
    [property: JsonPropertyName("capacity_note")] string? CapacityNote);

public sealed record CapacityRejectBody(
    [property: JsonPropertyName("reason")] string Reason);
