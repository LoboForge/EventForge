using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EventForge.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventForge.Tests.Api;

public sealed class PublicEndpointTests : IClassFixture<PublicWebApplicationFactory>
{
    private readonly PublicWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PublicEndpointTests(PublicWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Plans_and_models_return_the_public_catalogs()
    {
        using var plansResponse = await _client.GetAsync("/v1/public/plans");
        plansResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var plans = JsonDocument.Parse(await plansResponse.Content.ReadAsStringAsync());
        var rows = plans.RootElement.GetProperty("plans").EnumerateArray().ToList();
        rows.Select(p => p.GetProperty("id").GetString())
            .Should().Equal("starter", "pro", "scale");
        rows[0].GetProperty("price_usd").GetDecimal().Should().Be(29m);
        rows[0].GetProperty("credits").GetInt64().Should().Be(1_000);
        plans.RootElement.GetProperty("custom").GetProperty("enterprise_contact").GetString()
            .Should().Be("sales@loboforge.com");

        using var modelsResponse = await _client.GetAsync("/v1/public/models");
        modelsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var models = JsonDocument.Parse(await modelsResponse.Content.ReadAsStringAsync());
        var modelRows = models.RootElement.GetProperty("models").EnumerateArray().ToList();
        modelRows.Should().HaveCount(10);
        modelRows.Select(m => m.GetProperty("id").GetString())
            .Should().Contain(["wan2t2v", "wan2", "flux2klein", "ltx23", "dolphin"]);
        (await modelsResponse.Content.ReadAsStringAsync()).Should().NotContain("gpu");
    }

    [Fact]
    public async Task Register_login_duplicate_and_bad_password_follow_contract()
    {
        var email = $"buyer-{Guid.NewGuid():N}@example.com";
        using var badRegister = await _client.PostAsJsonAsync(
            "/v1/public/register",
            new { email, password = "short" });
        badRegister.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var register = await _client.PostAsJsonAsync(
            "/v1/public/register",
            new { email, password = "correct-horse", company = "Example Co" });
        register.StatusCode.Should().Be(HttpStatusCode.OK);
        using var registered = JsonDocument.Parse(await register.Content.ReadAsStringAsync());
        registered.RootElement.GetProperty("email").GetString().Should().Be(email);
        registered.RootElement.GetProperty("account_id").GetString().Should().NotBeNullOrWhiteSpace();

        using var duplicate = await _client.PostAsJsonAsync(
            "/v1/public/register",
            new { email = email.ToUpperInvariant(), password = "another-password" });
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await duplicate.Content.ReadAsStringAsync()).Should().Contain("email_exists");

        using var badLogin = await _client.PostAsJsonAsync(
            "/v1/public/login",
            new { email, password = "wrong-password" });
        badLogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var login = await _client.PostAsJsonAsync(
            "/v1/public/login",
            new { email, password = "correct-horse" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using var loggedIn = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        loggedIn.RootElement.GetProperty("session_token").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("paypal")]
    [InlineData("nowpayments")]
    public async Task Automated_checkout_returns_410_manual_billing(string provider)
    {
        var session = await RegisterAndLoginAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/public/checkout")
        {
            Content = JsonContent.Create(new { plan_id = "starter", provider }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        using var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await response.Content.ReadAsStringAsync()).Should().Contain("manual_billing");
    }

    [Fact]
    public async Task NowPayments_webhook_is_gone()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/public/webhooks/nowpayments")
        {
            Content = new StringContent(
                """{"order_id":"not-real","payment_status":"finished"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-nowpayments-sig", new string('0', 128));
        using var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await response.Content.ReadAsStringAsync()).Should().Contain("manual_billing");
    }

    [Fact]
    public async Task Capacity_request_can_be_created_listed_and_approved()
    {
        var email = $"capacity-{Guid.NewGuid():N}@example.com";
        using var created = await _client.PostAsJsonAsync(
            "/v1/public/capacity-request",
            new
            {
                email,
                company = "Capacity Co",
                name = "Ada",
                models = new[] { "flux2klein", "wan2t2v" },
                estimated_jobs = 250,
                notes = "Launch estimate",
                preferred_payment = "wire",
            });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("request_id").GetString()!;
        createdJson.RootElement.GetProperty("status").GetString().Should().Be("received");

        using var listRequest = OpsRequest(HttpMethod.Get, "/v1/ops/capacity-requests?status=received");
        using var listed = await _client.SendAsync(listRequest);
        listed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await listed.Content.ReadAsStringAsync()).Should().Contain(email);

        using var approveRequest = OpsRequest(
            HttpMethod.Post,
            $"/v1/ops/capacity-requests/{requestId}/approve",
            new { credits = 500, api_key_email = true });
        using var approved = await _client.SendAsync(approveRequest);
        approved.StatusCode.Should().Be(HttpStatusCode.OK);
        using var approvedJson = JsonDocument.Parse(await approved.Content.ReadAsStringAsync());
        approvedJson.RootElement.GetProperty("status").GetString().Should().Be("approved");
        approvedJson.RootElement.GetProperty("api_key").GetString().Should().StartWith("efk_");
        approvedJson.RootElement.GetProperty("credits").GetInt64().Should().Be(500);
        approvedJson.RootElement.GetProperty("api_key_email_sent").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Wire_and_monero_instructions_are_stored_without_live_providers()
    {
        async Task<string> CreateAsync(string preferred)
        {
            using var response = await _client.PostAsJsonAsync(
                "/v1/public/capacity-request",
                new
                {
                    email = $"{preferred}-{Guid.NewGuid():N}@example.com",
                    models = new[] { "flux2klein" },
                    estimated_jobs = 50,
                    preferred_payment = preferred,
                });
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("request_id").GetString()!;
        }

        var wireId = await CreateAsync("wire");
        using var wireRequest = OpsRequest(
            HttpMethod.Post,
            $"/v1/ops/capacity-requests/{wireId}/payment-instructions",
            new { method = "wire", amount_hint_usd = 125 });
        using var wire = await _client.SendAsync(wireRequest);
        wire.StatusCode.Should().Be(HttpStatusCode.OK);
        var wireText = await wire.Content.ReadAsStringAsync();
        wireText.Should().Contain("Test Bank").And.Contain($"EF-{wireId}");

        var moneroId = await CreateAsync("monero");
        using var moneroRequest = OpsRequest(
            HttpMethod.Post,
            $"/v1/ops/capacity-requests/{moneroId}/payment-instructions",
            new { method = "monero", amount_hint_usd = 80 });
        using var monero = await _client.SendAsync(moneroRequest);
        monero.StatusCode.Should().Be(HttpStatusCode.OK);
        var moneroText = await monero.Content.ReadAsStringAsync();
        moneroText.Should().Contain("44AFFq5kSiGBoZ").And.Contain("EFXMR-");

        var paypalId = await CreateAsync("paypal");
        using var paypalRequest = OpsRequest(
            HttpMethod.Post,
            $"/v1/ops/capacity-requests/{paypalId}/payment-instructions",
            new { method = "paypal", invoice_url = "https://www.paypal.com/invoice/example", amount_hint_usd = 60 });
        using var paypal = await _client.SendAsync(paypalRequest);
        paypal.StatusCode.Should().Be(HttpStatusCode.OK);
        (await paypal.Content.ReadAsStringAsync())
            .Should().Contain("https://www.paypal.com/invoice/example").And.Contain("manually_attached");
    }

    [Fact]
    public async Task Activated_account_key_validates_as_an_app_api_key()
    {
        var session = await RegisterAndLoginAsync();
        var store = _factory.Services.GetRequiredService<AccountStore>();
        var checkoutId = Guid.NewGuid().ToString("N");
        await store.CreatePurchaseAsync(
            new PurchaseRecord(
                checkoutId,
                session.AccountId,
                "starter",
                "nowpayments",
                "invoice-test",
                1_000,
                29m,
                "pending",
                DateTimeOffset.UtcNow,
                null),
            CancellationToken.None);
        var completion = await store.CompletePurchaseAsync(
            checkoutId,
            session.AccountId,
            AccountService.CreateApiKey,
            CancellationToken.None);
        completion.Should().NotBeNull();
        completion!.ApiKey.Should().StartWith("efk_");

        using var accountRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/public/account");
        accountRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        using var accountResponse = await _client.SendAsync(accountRequest);
        accountResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var account = JsonDocument.Parse(await accountResponse.Content.ReadAsStringAsync());
        account.RootElement.GetProperty("credits").GetInt64().Should().Be(1_000);
        account.RootElement.GetProperty("api_key").GetString().Should().Be(completion.ApiKey);

        using var appRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
        appRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", completion.ApiKey);
        using var appResponse = await _client.SendAsync(appRequest);
        appResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var app = JsonDocument.Parse(await appResponse.Content.ReadAsStringAsync());
        app.RootElement.GetProperty("app_id").GetString().Should().Be(session.AccountId);
    }

    private async Task<(string AccountId, string Token)> RegisterAndLoginAsync()
    {
        var email = $"checkout-{Guid.NewGuid():N}@example.com";
        using var register = await _client.PostAsJsonAsync(
            "/v1/public/register",
            new { email, password = "valid-password" });
        register.EnsureSuccessStatusCode();
        using var registered = JsonDocument.Parse(await register.Content.ReadAsStringAsync());
        var accountId = registered.RootElement.GetProperty("account_id").GetString()!;

        using var login = await _client.PostAsJsonAsync(
            "/v1/public/login",
            new { email, password = "valid-password" });
        login.EnsureSuccessStatusCode();
        using var loggedIn = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return (accountId, loggedIn.RootElement.GetProperty("session_token").GetString()!);
    }

    private static HttpRequestMessage OpsRequest(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-EventForge-Ops-Key", "test-ops-key");
        if (body != null) request.Content = JsonContent.Create(body);
        return request;
    }
}

public sealed class PublicWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ef-public-test-{Guid.NewGuid():N}.db");
    private readonly string _artifactDir = Path.Combine(Path.GetTempPath(), $"ef-public-art-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EventForge:S3:LoadOnStartup"] = "false",
                ["EventForge:S3:Enabled"] = "false",
                ["EventForge:Artifacts:Enabled"] = "false",
                ["EventForge:SqlitePath"] = _dbPath,
                ["EventForge:LocalArtifactDir"] = _artifactDir,
                ["EventForge:PublicUrl"] = "http://localhost",
                ["EventForge:OpsKey"] = "test-ops-key",
                ["EventForge:Payments:PayPal:ClientId"] = "",
                ["EventForge:Payments:PayPal:Secret"] = "",
                ["EventForge:Payments:NowPayments:ApiKey"] = "",
                ["EventForge:Payments:NowPayments:IpnSecret"] = "test-ipn-secret",
                ["EventForge:Payments:Wire:BankName"] = "Test Bank",
                ["EventForge:Payments:Wire:AccountName"] = "EventForge Test",
                ["EventForge:Payments:Wire:ReferenceTemplate"] = "EF-{request_id}",
                ["EventForge:Payments:Monero:Enabled"] = "true",
                ["EventForge:Payments:Monero:ReceiveAddress"] = "44AFFq5kSiGBoZ-test-address",
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_artifactDir)) Directory.Delete(_artifactDir, true); } catch { }
    }
}
