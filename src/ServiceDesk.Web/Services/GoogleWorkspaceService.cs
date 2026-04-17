using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ServiceDesk.Core.Models;
using ServiceDesk.Infrastructure.Data;

namespace ServiceDesk.Web.Services;

/// <summary>
/// Manages Google Workspace interactions via a Service Account with Domain-Wide Delegation.
/// Uses raw HTTP + .NET RSA crypto — no extra NuGet packages needed.
/// </summary>
public class GoogleWorkspaceService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoogleWorkspaceService> _logger;

    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string GmailApiBase  = "https://gmail.googleapis.com/gmail/v1/users";
    private const string SignatureScope = "https://www.googleapis.com/auth/gmail.settings.basic";

    public GoogleWorkspaceService(
        IServiceScopeFactory scopeFactory,
        IDataProtectionProvider dpProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<GoogleWorkspaceService> logger)
    {
        _scopeFactory      = scopeFactory;
        _protector         = dpProvider.CreateProtector("GoogleWorkspace.v1");
        _httpClientFactory = httpClientFactory;
        _logger            = logger;
    }

    // ── Settings access ───────────────────────────────────────────────────────

    public async Task<GoogleWorkspaceSettings?> GetSettingsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();
        return await db.GoogleWorkspaceSettings.FirstOrDefaultAsync();
    }

    /// <summary>Save (or update) the settings record. Encrypts the service account JSON if provided.</summary>
    public async Task SaveSettingsAsync(string adminEmail, string domain,
        string? serviceAccountJson, string? signatureTemplate)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();

        var settings = await db.GoogleWorkspaceSettings.FirstOrDefaultAsync()
                       ?? new GoogleWorkspaceSettings();

        settings.AdminEmail        = adminEmail.Trim();
        settings.Domain            = domain.Trim().ToLower();
        settings.SignatureTemplate = signatureTemplate;
        settings.UpdatedDate       = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(serviceAccountJson))
        {
            settings.EncryptedServiceAccountJson = _protector.Protect(serviceAccountJson.Trim());
            settings.IsConfigured                = true;
        }

        if (settings.Id == 0)
            db.GoogleWorkspaceSettings.Add(settings);

        await db.SaveChangesAsync();
    }

    // ── Service Account JWT auth ──────────────────────────────────────────────

    /// <summary>
    /// Build a short-lived access token by impersonating <paramref name="userEmail"/> via DWD.
    /// Returns null if service account is not configured.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(string userEmail)
    {
        var settings = await GetSettingsAsync();
        if (settings?.EncryptedServiceAccountJson == null || !settings.IsConfigured)
            return null;

        string saJson;
        try { saJson = _protector.Unprotect(settings.EncryptedServiceAccountJson); }
        catch { return null; }

        ServiceAccountKey? key;
        try { key = JsonSerializer.Deserialize<ServiceAccountKey>(saJson); }
        catch { return null; }

        if (key == null || string.IsNullOrEmpty(key.private_key) || string.IsNullOrEmpty(key.client_email))
            return null;

        var jwt = BuildJwt(key.client_email, key.private_key_id ?? "", key.private_key, userEmail);
        return await ExchangeJwtForTokenAsync(jwt);
    }

    private static string BuildJwt(string clientEmail, string keyId, string privateKeyPem, string subjectEmail)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            alg = "RS256",
            typ = "JWT",
            kid = keyId
        }));

        var claims = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss   = clientEmail,
            sub   = subjectEmail,
            scope = SignatureScope,
            aud   = TokenEndpoint,
            iat   = now,
            exp   = now + 3600
        }));

        var signingInput = Encoding.UTF8.GetBytes($"{header}.{claims}");

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem.AsSpan());
        var sig = rsa.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{header}.{claims}.{Base64UrlEncode(sig)}";
    }

    private async Task<string?> ExchangeJwtForTokenAsync(string jwt)
    {
        using var client = _httpClientFactory.CreateClient();
        var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
            new KeyValuePair<string,string>("assertion",  jwt)
        });

        var resp = await client.PostAsync(TokenEndpoint, body);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("Google token exchange failed: {Error}", err);
            return null;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("access_token", out var tok)
            ? tok.GetString()
            : null;
    }

    // ── Gmail Signature API ───────────────────────────────────────────────────

    /// <summary>Get the current HTML signature for <paramref name="userEmail"/>.</summary>
    public async Task<(bool Success, string? Html, string? Error)> GetSignatureAsync(string userEmail)
    {
        var token = await GetAccessTokenAsync(userEmail);
        if (token == null)
            return (false, null, "Service account not configured or auth failed.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // The primary send-as address is the user's own email
        var url = $"{GmailApiBase}/{Uri.EscapeDataString(userEmail)}/settings/sendAs/{Uri.EscapeDataString(userEmail)}";
        var resp = await client.GetAsync(url);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("GetSignature failed for {User}: {Error}", userEmail, err);
            return (false, null, $"API error {(int)resp.StatusCode}: {err}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var html = doc.RootElement.TryGetProperty("signature", out var sig)
            ? sig.GetString() ?? ""
            : "";

        return (true, html, null);
    }

    /// <summary>Set the HTML signature for <paramref name="userEmail"/>.</summary>
    public async Task<(bool Success, string? Error)> UpdateSignatureAsync(string userEmail, string html)
    {
        var token = await GetAccessTokenAsync(userEmail);
        if (token == null)
            return (false, "Service account not configured or auth failed.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var url = $"{GmailApiBase}/{Uri.EscapeDataString(userEmail)}/settings/sendAs/{Uri.EscapeDataString(userEmail)}";
        var payload = JsonSerializer.Serialize(new { signature = html });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        // PATCH only updates the signature field
        var request = new HttpRequestMessage(new HttpMethod("PATCH"), url) { Content = content };
        var resp = await client.SendAsync(request);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("UpdateSignature failed for {User}: {Error}", userEmail, err);
            return (false, $"API error {(int)resp.StatusCode}: {err}");
        }

        return (true, null);
    }

    // ── Template rendering ────────────────────────────────────────────────────

    public Task<string> RenderTemplateAsync(string template, Employee employee)
    {
        var branchName  = employee.Branch?.Name  ?? "";
        var branchPhone = employee.Branch?.Phone ?? "";

        static string Slug(string s) => s.Replace(" ", "_");

        return Task.FromResult(template
            .Replace("{FIRST_NAME}",      employee.FirstName)
            .Replace("{LAST_NAME}",       employee.LastName)
            .Replace("{FULL_NAME}",       employee.FullName)
            .Replace("{FIRST_NAME_SLUG}", Slug(employee.FirstName))
            .Replace("{LAST_NAME_SLUG}",  Slug(employee.LastName))
            .Replace("{JOB_TITLE}",       employee.JobTitle  ?? "")
            .Replace("{EMAIL}",           employee.Email)
            .Replace("{EMAIL_DISPLAY}",   employee.Email.Replace(".org", ".com"))
            .Replace("{PHONE}",           employee.Phone     ?? "")
            .Replace("{DEPARTMENT}",      employee.Department)
            .Replace("{BRANCH}",          branchName)
            .Replace("{BRANCH_PHONE}",    branchPhone));
    }

    // ── Test connection ───────────────────────────────────────────────────────

    /// <summary>Verify DWD by fetching the admin user's send-as list. Returns (passed, message).</summary>
    public async Task<(bool Passed, string Message)> TestConnectionAsync()
    {
        var settings = await GetSettingsAsync();
        if (settings == null || !settings.IsConfigured)
            return (false, "Google Workspace is not configured. Please upload a service account key.");

        var token = await GetAccessTokenAsync(settings.AdminEmail);
        if (token == null)
            return (false, "Failed to obtain access token. Check service account key and DWD configuration.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var url = $"{GmailApiBase}/{Uri.EscapeDataString(settings.AdminEmail)}/settings/sendAs";
        var resp = await client.GetAsync(url);

        string message;
        bool passed;
        if (resp.IsSuccessStatusCode)
        {
            passed  = true;
            message = $"Connected successfully as {settings.AdminEmail}. Domain-Wide Delegation is working.";
        }
        else
        {
            var body = await resp.Content.ReadAsStringAsync();
            passed  = false;
            message = $"API call failed ({(int)resp.StatusCode}): {body}";
        }

        // Persist test result
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();
        var s = await db.GoogleWorkspaceSettings.FirstOrDefaultAsync();
        if (s != null)
        {
            s.LastTestedDate  = DateTime.UtcNow;
            s.LastTestResult  = message[..Math.Min(message.Length, 500)];
            s.LastTestPassed  = passed;
            await db.SaveChangesAsync();
        }

        return (passed, message);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    // ── Inner types ───────────────────────────────────────────────────────────

    private sealed class ServiceAccountKey
    {
        public string? type           { get; set; }
        public string? project_id     { get; set; }
        public string? private_key_id { get; set; }
        public string? private_key    { get; set; }
        public string? client_email   { get; set; }
        public string? client_id      { get; set; }
    }
}
