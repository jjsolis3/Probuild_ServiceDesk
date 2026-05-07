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

    private const string TokenEndpoint    = "https://oauth2.googleapis.com/token";
    private const string GmailApiBase     = "https://gmail.googleapis.com/gmail/v1/users";
    private const string AdminApiBase     = "https://admin.googleapis.com/admin/directory/v1";
    private const string ReportsApiBase   = "https://admin.googleapis.com/admin/reports/v1";
    private const string DriveApiBase     = "https://www.googleapis.com/drive/v3";
    private const string DataTransferBase = "https://admin.googleapis.com/admin/datatransfer/v1";

    private const string SignatureScope   = "https://www.googleapis.com/auth/gmail.settings.basic";

    // Core Admin SDK scopes — these 3 must be in DWD for basic Workspace features to work
    private const string AdminScope =
        "https://www.googleapis.com/auth/admin.directory.user " +
        "https://www.googleapis.com/auth/admin.directory.group " +
        "https://www.googleapis.com/auth/admin.reports.usage.readonly";

    // Extended scopes — each added to DWD separately to unlock advanced features
    private const string SecurityTokensScope = "https://www.googleapis.com/auth/admin.directory.user.security";
    private const string AuditReportScope    = "https://www.googleapis.com/auth/admin.reports.audit.readonly";
    private const string DataTransferScope   =
        "https://www.googleapis.com/auth/admin.datatransfer " +
        "https://www.googleapis.com/auth/admin.directory.user";

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
        return await db.GoogleWorkspaceSettings.OrderBy(s => s.Id).FirstOrDefaultAsync();
    }

    /// <summary>Save (or update) the settings record. Encrypts the service account JSON if provided.</summary>
    public async Task SaveSettingsAsync(string adminEmail, string domain,
        string? serviceAccountJson, string? signatureTemplate)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDeskDbContext>();

        var settings = await db.GoogleWorkspaceSettings.OrderBy(s => s.Id).FirstOrDefaultAsync()
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
    public async Task<string?> GetAccessTokenAsync(string userEmail, string scope = SignatureScope)
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

        var jwt = BuildJwt(key.client_email, key.private_key_id ?? "", key.private_key, userEmail, scope);
        return await ExchangeJwtForTokenAsync(jwt);
    }

    /// <summary>Admin-scoped token — impersonates the admin user for Directory/Reports API calls.</summary>
    private async Task<string?> GetAdminAccessTokenAsync()
    {
        var settings = await GetSettingsAsync();
        if (settings == null || string.IsNullOrEmpty(settings.AdminEmail)) return null;
        return await GetAccessTokenAsync(settings.AdminEmail, AdminScope);
    }

    private static string BuildJwt(string clientEmail, string keyId, string privateKeyPem,
        string subjectEmail, string scope = SignatureScope)
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
            scope = scope,
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

    /// <summary>List all send-as addresses (primary + verified aliases) for <paramref name="userEmail"/>.</summary>
    public async Task<(bool Success, List<string> Addresses, string? Error)> GetSendAsAddressesAsync(string userEmail)
    {
        var token = await GetAccessTokenAsync(userEmail);
        if (token == null)
            return (false, [], "Service account not configured or auth failed.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var url  = $"{GmailApiBase}/{Uri.EscapeDataString(userEmail)}/settings/sendAs";
        var resp = await client.GetAsync(url);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("GetSendAs failed for {User}: {Error}", userEmail, err);
            return (false, [], $"API error {(int)resp.StatusCode}: {err}");
        }

        using var doc  = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var addresses  = new List<string>();

        if (doc.RootElement.TryGetProperty("sendAs", out var arr))
        {
            foreach (var entry in arr.EnumerateArray())
            {
                if (entry.TryGetProperty("sendAsEmail", out var emailProp))
                {
                    var addr = emailProp.GetString();
                    if (!string.IsNullOrEmpty(addr))
                        addresses.Add(addr);
                }
            }
        }

        return (true, addresses, null);
    }

    /// <summary>
    /// Set <paramref name="html"/> as the signature on the primary address AND every verified alias.
    /// Returns how many addresses were updated vs failed.
    /// </summary>
    public async Task<(bool Success, int Updated, int Failed, string? Error)> UpdateSignatureAllAddressesAsync(
        string userEmail, string html)
    {
        var (listOk, addresses, _) = await GetSendAsAddressesAsync(userEmail);

        // Fall back to primary-only if listing fails
        if (!listOk || addresses.Count == 0)
            addresses = [userEmail];

        // Ensure primary is included
        if (!addresses.Contains(userEmail, StringComparer.OrdinalIgnoreCase))
            addresses.Insert(0, userEmail);

        var token = await GetAccessTokenAsync(userEmail);
        if (token == null)
            return (false, 0, addresses.Count, "Service account not configured or auth failed.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        int updated = 0, failed = 0;

        foreach (var addr in addresses)
        {
            var url     = $"{GmailApiBase}/{Uri.EscapeDataString(userEmail)}/settings/sendAs/{Uri.EscapeDataString(addr)}";
            var payload = JsonSerializer.Serialize(new { signature = html });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var req     = new HttpRequestMessage(new HttpMethod("PATCH"), url) { Content = content };
            var resp    = await client.SendAsync(req);

            if (resp.IsSuccessStatusCode)
                updated++;
            else
            {
                failed++;
                var err = await resp.Content.ReadAsStringAsync();
                _logger.LogWarning("UpdateSignature failed for {User}/{Addr}: {Error}", userEmail, addr, err);
            }
        }

        return (updated > 0, updated, failed,
            failed > 0 ? $"{failed} address(es) could not be updated." : null);
    }

    /// <summary>Render and push the signature template for every employee in <paramref name="employees"/>.</summary>
    public async Task<(int Success, int Failed, int Skipped, List<BulkSignatureResult> Results)>
        BulkApplySignatureAsync(IList<Employee> employees, string template, bool includeAliases)
    {
        int success = 0, failed = 0, skipped = 0;
        var results = new List<BulkSignatureResult>();

        foreach (var emp in employees)
        {
            if (string.IsNullOrWhiteSpace(emp.Email))
            {
                skipped++;
                results.Add(new BulkSignatureResult(emp.FullName, "", "skipped", "No email address."));
                continue;
            }

            var html = await RenderTemplateAsync(template, emp);

            if (includeAliases)
            {
                var (ok, updated, _, err) = await UpdateSignatureAllAddressesAsync(emp.Email, html);
                if (ok) { success++; results.Add(new BulkSignatureResult(emp.FullName, emp.Email, "success", $"Updated {updated} address(es).")); }
                else    { failed++;  results.Add(new BulkSignatureResult(emp.FullName, emp.Email, "failed",  err ?? "Unknown error.")); }
            }
            else
            {
                var (ok, err) = await UpdateSignatureAsync(emp.Email, html);
                if (ok) { success++; results.Add(new BulkSignatureResult(emp.FullName, emp.Email, "success", "Signature updated.")); }
                else    { failed++;  results.Add(new BulkSignatureResult(emp.FullName, emp.Email, "failed",  err ?? "Unknown error.")); }
            }
        }

        return (success, failed, skipped, results);
    }

    // ── Template rendering ────────────────────────────────────────────────────

    public Task<string> RenderTemplateAsync(string template, Employee employee)
    {
        var branchName    = employee.Branch?.Name       ?? "";
        var branchPhone   = employee.Branch?.Phone      ?? "";
        var costCenter    = employee.Branch?.CostCenter ?? "";
        var buildingId    = employee.Branch?.BuildingId ?? "";

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
            .Replace("{BRANCH_PHONE}",    branchPhone)
            .Replace("{COST_CENTER}",     costCenter)
            .Replace("{BUILDING_ID}",     buildingId));
    }

    // ── Admin Directory — User management ────────────────────────────────────

    /// <summary>Fetch the Google account state (suspended flag, name, org unit) for a user.</summary>
    public async Task<(bool Success, GoogleUserInfo? User, string? Error)> GetGoogleUserAsync(string userEmail)
    {
        if (string.IsNullOrWhiteSpace(userEmail))
            return (false, null, "Employee has no email address.");

        var token = await GetAdminAccessTokenAsync();
        if (token == null) return (false, null, "Admin service account not configured or auth failed.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var url  = $"{AdminApiBase}/users/{Uri.EscapeDataString(userEmail)}?fields=id,primaryEmail,name,suspended,changePasswordAtNextLogin,orgUnitPath";
        var resp = await client.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("GetGoogleUser failed for {User}: {Error}", userEmail, err);
            return (false, null, $"API error {(int)resp.StatusCode}: {err}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root      = doc.RootElement;
        var fullName  = root.TryGetProperty("name", out var nameEl)
            ? nameEl.TryGetProperty("fullName", out var fn) ? fn.GetString() ?? "" : ""
            : "";
        var info = new GoogleUserInfo(
            Email:                     root.TryGetProperty("primaryEmail", out var pe) ? pe.GetString() ?? userEmail : userEmail,
            FullName:                  fullName,
            Suspended:                 root.TryGetProperty("suspended", out var sus) && sus.GetBoolean(),
            ChangePasswordAtNextLogin: root.TryGetProperty("changePasswordAtNextLogin", out var cpnl) && cpnl.GetBoolean(),
            OrgUnit:                   root.TryGetProperty("orgUnitPath", out var ou) ? ou.GetString() : null
        );
        return (true, info, null);
    }

    /// <summary>List all users in the configured Google Workspace domain.
    /// Pages through results so orgs with > 500 users are fully covered.</summary>
    public async Task<(bool Success, List<GoogleDirectoryUser> Users, string? Error)> ListDomainUsersAsync()
    {
        var settings = await GetSettingsAsync();
        if (settings == null || string.IsNullOrWhiteSpace(settings.Domain))
            return (false, [], "Google Workspace not configured.");

        var token = await GetAdminAccessTokenAsync();
        if (token == null) return (false, [], "Admin service account not configured or auth failed.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var users    = new List<GoogleDirectoryUser>();
        string? page = null;
        int safetyCap = 20; // hard ceiling: 20 pages × 500 = 10,000 users

        do
        {
            var url  = $"{AdminApiBase}/users?domain={Uri.EscapeDataString(settings.Domain)}" +
                       $"&maxResults=500&projection=full&orderBy=email" +
                       (page != null ? $"&pageToken={Uri.EscapeDataString(page)}" : "");
            var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                _logger.LogWarning("ListDomainUsers failed: {Error}", err);
                return (false, users, $"API error {(int)resp.StatusCode}: {err}");
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            if (root.TryGetProperty("users", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in arr.EnumerateArray())
                {
                    var email     = u.TryGetProperty("primaryEmail", out var pe) ? pe.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(email)) continue;

                    var firstName = "";
                    var lastName  = "";
                    var fullName  = "";
                    if (u.TryGetProperty("name", out var nameEl))
                    {
                        firstName = nameEl.TryGetProperty("givenName",  out var gn) ? gn.GetString() ?? "" : "";
                        lastName  = nameEl.TryGetProperty("familyName", out var fn) ? fn.GetString() ?? "" : "";
                        fullName  = nameEl.TryGetProperty("fullName",   out var ful) ? ful.GetString() ?? "" : "";
                    }

                    string? jobTitle  = null;
                    string? department = null;
                    if (u.TryGetProperty("organizations", out var orgs) && orgs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var o in orgs.EnumerateArray())
                        {
                            if (jobTitle == null && o.TryGetProperty("title", out var t))
                                jobTitle = t.GetString();
                            if (department == null && o.TryGetProperty("department", out var d))
                                department = d.GetString();
                            if (jobTitle != null && department != null) break;
                        }
                    }

                    string? phone = null;
                    if (u.TryGetProperty("phones", out var phones) && phones.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in phones.EnumerateArray())
                        {
                            if (p.TryGetProperty("value", out var v))
                            {
                                phone = v.GetString();
                                break;
                            }
                        }
                    }

                    var suspended = u.TryGetProperty("suspended", out var sus) && sus.GetBoolean();
                    var orgUnit   = u.TryGetProperty("orgUnitPath", out var ouEl) ? ouEl.GetString() : null;

                    users.Add(new GoogleDirectoryUser(
                        Email:      email,
                        FirstName:  firstName,
                        LastName:   lastName,
                        FullName:   string.IsNullOrEmpty(fullName) ? $"{firstName} {lastName}".Trim() : fullName,
                        JobTitle:   string.IsNullOrWhiteSpace(jobTitle)   ? null : jobTitle,
                        Department: string.IsNullOrWhiteSpace(department) ? null : department,
                        Phone:      string.IsNullOrWhiteSpace(phone)      ? null : phone,
                        Suspended:  suspended,
                        OrgUnit:    orgUnit
                    ));
                }
            }

            page = root.TryGetProperty("nextPageToken", out var npt) ? npt.GetString() : null;
            safetyCap--;
        }
        while (!string.IsNullOrEmpty(page) && safetyCap > 0);

        return (true, users, null);
    }

    /// <summary>Update Google Workspace user profile fields (org info, location, manager).</summary>
    public async Task<(bool Success, string? Error)> UpdateUserInfoAsync(string userEmail,
        string? jobTitle, string? department, string? costCenter, string? employeeType,
        string? buildingId, string? floorName, string? floorSection, string? managerEmail)
    {
        var token = await GetAdminAccessTokenAsync();
        if (token == null) return (false, "Admin service account not configured or auth failed.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var payload = new Dictionary<string, object>();

        if (jobTitle != null || department != null || costCenter != null || employeeType != null)
        {
            payload["organizations"] = new[]
            {
                new
                {
                    title        = jobTitle       ?? "",
                    department   = department     ?? "",
                    costCenter   = costCenter     ?? "",
                    type         = string.IsNullOrWhiteSpace(employeeType) ? "unknown" : employeeType,
                    primary      = true
                }
            };
        }

        if (buildingId != null || floorName != null || floorSection != null)
        {
            payload["locations"] = new[]
            {
                new
                {
                    type         = "desk",
                    buildingId   = buildingId   ?? "",
                    floorName    = floorName    ?? "",
                    floorSection = floorSection ?? ""
                }
            };
        }

        if (!string.IsNullOrWhiteSpace(managerEmail))
        {
            payload["relations"] = new[]
            {
                new { value = managerEmail, type = "manager" }
            };
        }

        if (payload.Count == 0) return (true, null);

        var url     = $"{AdminApiBase}/users/{Uri.EscapeDataString(userEmail)}";
        var json    = JsonSerializer.Serialize(payload);
        var req     = new HttpRequestMessage(new HttpMethod("PATCH"), url)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        var resp    = await client.SendAsync(req);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("UpdateUserInfo failed for {User}: {Error}", userEmail, err);
            return (false, $"API error {(int)resp.StatusCode}: {err}");
        }

        return (true, null);
    }

    /// <summary>Suspend or unsuspend a Google Workspace user account.</summary>
    public async Task<(bool Success, string? Error)> SetSuspendedAsync(string userEmail, bool suspended)
    {
        var token = await GetAdminAccessTokenAsync();
        if (token == null) return (false, "Admin service account not configured or auth failed.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var url     = $"{AdminApiBase}/users/{Uri.EscapeDataString(userEmail)}";
        var payload = JsonSerializer.Serialize(new { suspended });
        var req     = new HttpRequestMessage(new HttpMethod("PATCH"), url)
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        var resp = await client.SendAsync(req);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("SetSuspended({Suspended}) failed for {User}: {Error}", suspended, userEmail, err);
            return (false, $"API error {(int)resp.StatusCode}: {err}");
        }
        return (true, null);
    }

    /// <summary>
    /// Generate a temporary password, set it on the account, and force change on next login.
    /// Returns the plaintext temp password so the admin can communicate it to the employee.
    /// </summary>
    public async Task<(bool Success, string? TempPassword, string? Error)> ResetPasswordAsync(string userEmail)
    {
        var token = await GetAdminAccessTokenAsync();
        if (token == null) return (false, null, "Admin service account not configured or auth failed.");

        var tempPw = GenerateTempPassword();

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var url     = $"{AdminApiBase}/users/{Uri.EscapeDataString(userEmail)}";
        var payload = JsonSerializer.Serialize(new { password = tempPw, changePasswordAtNextLogin = true });
        var req     = new HttpRequestMessage(new HttpMethod("PATCH"), url)
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        var resp    = await client.SendAsync(req);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("ResetPassword failed for {User}: {Error}", userEmail, err);
            return (false, null, $"API error {(int)resp.StatusCode}: {err}");
        }
        return (true, tempPw, null);
    }

    // ── Gmail — Vacation / Out-of-Office ─────────────────────────────────────

    /// <summary>Get the current vacation / out-of-office responder settings for a user.</summary>
    public async Task<(bool Success, VacationResponder? Settings, string? Error)> GetVacationResponderAsync(string userEmail)
    {
        if (string.IsNullOrWhiteSpace(userEmail))
            return (false, null, "Employee has no email address.");

        var token = await GetAccessTokenAsync(userEmail);
        if (token == null) return (false, null, "Service account not configured or auth failed.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync($"{GmailApiBase}/{Uri.EscapeDataString(userEmail)}/settings/vacation");
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            return (false, null, $"API error {(int)resp.StatusCode}: {err}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var r         = doc.RootElement;
        DateTimeOffset? start = null;
        DateTimeOffset? end   = null;
        try
        {
            if (r.TryGetProperty("startTime", out var st) && st.TryGetInt64(out var stMs) && stMs > 0)
                start = DateTimeOffset.FromUnixTimeMilliseconds(stMs);
            if (r.TryGetProperty("endTime",   out var et) && et.TryGetInt64(out var etMs) && etMs > 0)
                end   = DateTimeOffset.FromUnixTimeMilliseconds(etMs);
        }
        catch { /* ignore out-of-range timestamps */ }

        var vac = new VacationResponder(
            EnableAutoReply:    r.TryGetProperty("enableAutoReply",    out var ear)  && ear.GetBoolean(),
            ResponseSubject:    r.TryGetProperty("responseSubject",    out var rs)   ? rs.GetString()  : null,
            ResponseBodyHtml:   r.TryGetProperty("responseBodyHtml",   out var rbh)  ? rbh.GetString() : null,
            StartTime:          start,
            EndTime:            end,
            RestrictToContacts: r.TryGetProperty("restrictToContacts", out var rtc)  && rtc.GetBoolean(),
            RestrictToDomain:   r.TryGetProperty("restrictToDomain",   out var rtd)  && rtd.GetBoolean()
        );
        return (true, vac, null);
    }

    /// <summary>Update (or clear) the vacation responder for a user.</summary>
    public async Task<(bool Success, string? Error)> SetVacationResponderAsync(string userEmail, VacationResponder settings)
    {
        var token = await GetAccessTokenAsync(userEmail);
        if (token == null) return (false, "Service account not configured or auth failed.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var body = new Dictionary<string, object> { ["enableAutoReply"] = settings.EnableAutoReply };
        if (settings.ResponseSubject  != null) body["responseSubject"]  = settings.ResponseSubject;
        if (settings.ResponseBodyHtml != null) body["responseBodyHtml"] = settings.ResponseBodyHtml;
        if (settings.StartTime.HasValue)       body["startTime"]        = settings.StartTime.Value.ToUnixTimeMilliseconds();
        if (settings.EndTime.HasValue)         body["endTime"]          = settings.EndTime.Value.ToUnixTimeMilliseconds();
        body["restrictToContacts"] = settings.RestrictToContacts;
        body["restrictToDomain"]   = settings.RestrictToDomain;

        var payload = JsonSerializer.Serialize(body);
        var url     = $"{GmailApiBase}/{Uri.EscapeDataString(userEmail)}/settings/vacation";
        var resp    = await client.PutAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("SetVacationResponder failed for {User}: {Error}", userEmail, err);
            return (false, $"API error {(int)resp.StatusCode}: {err}");
        }
        return (true, null);
    }

    // ── Admin Reports — Drive / Gmail storage ─────────────────────────────────

    /// <summary>
    /// Retrieve the user's Gmail + Drive storage usage from the Reports API (data may be 1–2 days old).
    /// </summary>
    public async Task<(bool Success, DriveStorageInfo? Info, string? Error)> GetStorageAsync(string userEmail)
    {
        var token = await GetAdminAccessTokenAsync();
        if (token == null) return (false, null, "Admin service account not configured or auth failed.");

        // Reports API: try yesterday first, fall back to day before
        for (int daysAgo = 1; daysAgo <= 3; daysAgo++)
        {
            var date = DateTime.UtcNow.AddDays(-daysAgo).ToString("yyyy-MM-dd");
            var url  = $"{ReportsApiBase}/usage/users/{Uri.EscapeDataString(userEmail)}/dates/{date}" +
                       "?parameters=gmail:gmail_used_quota_in_mb,drive:storage_used";

            using var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode) continue;

            using var doc    = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var reports      = doc.RootElement.TryGetProperty("usageReports", out var rpts) ? rpts : default;
            if (reports.ValueKind != JsonValueKind.Array || reports.GetArrayLength() == 0) continue;

            long gmailMb = 0, driveMb = 0;
            if (reports[0].TryGetProperty("parameters", out var parms))
            {
                foreach (var p in parms.EnumerateArray())
                {
                    var name = p.TryGetProperty("name", out var n) ? n.GetString() : "";
                    var val  = p.TryGetProperty("intValue", out var iv) ? iv.GetString() : "0";
                    if (name == "gmail:gmail_used_quota_in_mb") long.TryParse(val, out gmailMb);
                    if (name == "drive:storage_used")           long.TryParse(val, out driveMb);
                }
            }

            // Also fetch the user's quota limit from Directory API
            long quotaMb = 15360; // default 15 GB
            var uToken = await GetAdminAccessTokenAsync();
            if (uToken != null)
            {
                using var uc = _httpClientFactory.CreateClient();
                uc.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", uToken);
                var uResp = await uc.GetAsync($"{AdminApiBase}/users/{Uri.EscapeDataString(userEmail)}?fields=storageQuota");
                if (uResp.IsSuccessStatusCode)
                {
                    using var ud = JsonDocument.Parse(await uResp.Content.ReadAsStringAsync());
                    if (ud.RootElement.TryGetProperty("storageQuota", out var sq) &&
                        sq.TryGetProperty("limit", out var lim) && lim.TryGetInt64(out var limitBytes))
                        quotaMb = limitBytes / 1024 / 1024;
                }
            }

            return (true, new DriveStorageInfo(gmailMb, driveMb, quotaMb, DateTime.UtcNow.AddDays(-daysAgo)), null);
        }
        return (false, null, "Storage usage data not available yet (Reports API has a 1–3 day delay).");
    }

    // ── Admin Directory — Groups ──────────────────────────────────────────────

    /// <summary>List all groups the user belongs to.</summary>
    public async Task<(bool Success, List<GroupInfo> Groups, string? Error)> GetUserGroupsAsync(string userEmail)
    {
        if (string.IsNullOrWhiteSpace(userEmail))
            return (false, [], "Employee has no email address.");

        var token = await GetAdminAccessTokenAsync();
        if (token == null) return (false, [], "Admin service account not configured or auth failed.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var url  = $"{AdminApiBase}/groups?userKey={Uri.EscapeDataString(userEmail)}&maxResults=200";
        var resp = await client.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            return (false, [], $"API error {(int)resp.StatusCode}: {err}");
        }

        return (true, ParseGroups(await resp.Content.ReadAsStringAsync()), null);
    }

    /// <summary>List all groups in the configured domain.</summary>
    public async Task<(bool Success, List<GroupInfo> Groups, string? Error)> GetDomainGroupsAsync()
    {
        var settings = await GetSettingsAsync();
        if (settings == null) return (false, [], "Google Workspace not configured.");

        var token = await GetAdminAccessTokenAsync();
        if (token == null) return (false, [], "Admin service account not configured or auth failed.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var url  = $"{AdminApiBase}/groups?domain={Uri.EscapeDataString(settings.Domain)}&maxResults=200&orderBy=email";
        var resp = await client.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            return (false, [], $"API error {(int)resp.StatusCode}: {err}");
        }

        return (true, ParseGroups(await resp.Content.ReadAsStringAsync()), null);
    }

    /// <summary>Add a user to a group as a MEMBER.</summary>
    public async Task<(bool Success, string? Error)> AddToGroupAsync(string groupEmail, string userEmail)
    {
        var token = await GetAdminAccessTokenAsync();
        if (token == null) return (false, "Admin service account not configured or auth failed.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var url     = $"{AdminApiBase}/groups/{Uri.EscapeDataString(groupEmail)}/members";
        var payload = JsonSerializer.Serialize(new { email = userEmail, role = "MEMBER" });
        var resp    = await client.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("AddToGroup {Group} failed for {User}: {Error}", groupEmail, userEmail, err);
            return (false, $"API error {(int)resp.StatusCode}: {err}");
        }
        return (true, null);
    }

    /// <summary>Remove a user from a group.</summary>
    public async Task<(bool Success, string? Error)> RemoveFromGroupAsync(string groupEmail, string userEmail)
    {
        var token = await GetAdminAccessTokenAsync();
        if (token == null) return (false, "Admin service account not configured or auth failed.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var url  = $"{AdminApiBase}/groups/{Uri.EscapeDataString(groupEmail)}/members/{Uri.EscapeDataString(userEmail)}";
        var resp = await client.DeleteAsync(url);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("RemoveFromGroup {Group} failed for {User}: {Error}", groupEmail, userEmail, err);
            return (false, $"API error {(int)resp.StatusCode}: {err}");
        }
        return (true, null);
    }

    // ── Admin Directory — User provisioning (Onboarding) ─────────────────────

    /// <summary>
    /// Create a new Google Workspace user account for an employee.
    /// The account will be set to require a password change on first login.
    /// </summary>
    public async Task<(bool Success, string? TempPassword, string? Error)> CreateGoogleUserAsync(
        string firstName, string lastName, string email, string orgUnit, string? tempPassword = null)
    {
        var token = await GetAdminAccessTokenAsync();
        if (token == null) return (false, null, "Admin service account not configured or auth failed.");

        var pw = tempPassword ?? GenerateTempPassword();

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var payload = JsonSerializer.Serialize(new
        {
            name = new { givenName = firstName, familyName = lastName },
            primaryEmail = email,
            password = pw,
            changePasswordAtNextLogin = true,
            orgUnitPath = orgUnit.StartsWith('/') ? orgUnit : "/" + orgUnit
        });

        var resp = await client.PostAsync($"{AdminApiBase}/users",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("CreateGoogleUser failed for {Email}: {Error}", email, err);
            return (false, null, $"API error {(int)resp.StatusCode}: {err}");
        }
        return (true, pw, null);
    }

    // ── Offboarding helpers ───────────────────────────────────────────────────

    /// <summary>Revoke all OAuth 2.0 access tokens issued to the user (removes all third-party app access).</summary>
    public async Task<(bool Success, string? Error)> RevokeAllTokensAsync(string userEmail)
    {
        var settings = await GetSettingsAsync();
        if (settings == null || string.IsNullOrEmpty(settings.AdminEmail))
            return (false, "Admin service account not configured or auth failed.");
        var token = await GetAccessTokenAsync(settings.AdminEmail, SecurityTokensScope);
        if (token == null) return (false, "Auth failed — ensure the 'admin.directory.user.security' scope is added to DWD.");

        // List all tokens first
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var listUrl  = $"{AdminApiBase}/users/{Uri.EscapeDataString(userEmail)}/tokens";
        var listResp = await client.GetAsync(listUrl);
        if (!listResp.IsSuccessStatusCode)
        {
            if ((int)listResp.StatusCode == 404)
                return (true, null); // No tokens found — success
            var err = await listResp.Content.ReadAsStringAsync();
            return (false, $"Token list error {(int)listResp.StatusCode}: {err}");
        }

        using var doc  = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var items      = doc.RootElement.TryGetProperty("items", out var arr) ? arr : default;
        if (items.ValueKind != JsonValueKind.Array) return (true, null);

        int errors = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("clientId", out var cid)) continue;
            var clientId = Uri.EscapeDataString(cid.GetString() ?? "");
            if (string.IsNullOrEmpty(clientId)) continue;

            var delUrl  = $"{AdminApiBase}/users/{Uri.EscapeDataString(userEmail)}/tokens/{clientId}";
            var delResp = await client.DeleteAsync(delUrl);
            if (!delResp.IsSuccessStatusCode) errors++;
        }

        return errors == 0
            ? (true, null)
            : (false, $"{errors} token(s) could not be revoked.");
    }

    /// <summary>List available shared drives in the domain (used for Drive transfer destination picker).</summary>
    public async Task<(bool Success, List<SharedDriveInfo> Drives, string? Error)> ListSharedDrivesAsync()
    {
        var settings = await GetSettingsAsync();
        if (settings == null || string.IsNullOrEmpty(settings.AdminEmail))
            return (false, [], "Google Workspace not configured.");

        // Drive API requires impersonating a user; use the admin account
        var token = await GetAccessTokenAsync(settings.AdminEmail,
            "https://www.googleapis.com/auth/drive.readonly");
        if (token == null) return (false, [], "Drive access token failed.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var url  = $"{DriveApiBase}/drives?pageSize=100&useDomainAdminAccess=true";
        var resp = await client.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            return (false, [], $"Drive API error {(int)resp.StatusCode}: {err}");
        }

        var list = new List<SharedDriveInfo>();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("drives", out var drives))
        {
            foreach (var d in drives.EnumerateArray())
            {
                var id   = d.TryGetProperty("id",   out var di) ? di.GetString() ?? "" : "";
                var name = d.TryGetProperty("name", out var dn) ? dn.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(id))
                    list.Add(new SharedDriveInfo(id, name));
            }
        }
        return (true, list, null);
    }

    /// <summary>
    /// Initiate a Google Data Transfer to move a departing user's Drive files to another user or shared drive.
    /// <paramref name="toUserEmail"/> — destination email (user-to-user transfer).
    /// Returns the transfer ID for status polling.
    /// </summary>
    public async Task<(bool Success, string? TransferId, string? Error)> StartDriveTransferAsync(
        string fromEmail, string toUserEmail)
    {
        var settings = await GetSettingsAsync();
        if (settings == null || string.IsNullOrEmpty(settings.AdminEmail))
            return (false, null, "Admin service account not configured or auth failed.");
        var token = await GetAccessTokenAsync(settings.AdminEmail, DataTransferScope);
        if (token == null) return (false, null, "Auth failed — ensure 'admin.datatransfer' is added to DWD.");

        // Resolve destination user to their Google ID
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var userResp = await client.GetAsync($"{AdminApiBase}/users/{Uri.EscapeDataString(toUserEmail)}?fields=id");
        if (!userResp.IsSuccessStatusCode)
            return (false, null, $"Could not resolve destination user {toUserEmail}.");

        using var userDoc = JsonDocument.Parse(await userResp.Content.ReadAsStringAsync());
        var destId = userDoc.RootElement.TryGetProperty("id", out var uid) ? uid.GetString() : null;
        if (string.IsNullOrEmpty(destId)) return (false, null, "Destination user Google ID not found.");

        // Resolve source user Google ID
        var fromResp = await client.GetAsync($"{AdminApiBase}/users/{Uri.EscapeDataString(fromEmail)}?fields=id");
        if (!fromResp.IsSuccessStatusCode)
            return (false, null, $"Could not resolve source user {fromEmail}.");

        using var fromDoc = JsonDocument.Parse(await fromResp.Content.ReadAsStringAsync());
        var fromId = fromDoc.RootElement.TryGetProperty("id", out var fuid) ? fuid.GetString() : null;
        if (string.IsNullOrEmpty(fromId)) return (false, null, "Source user Google ID not found.");

        // Data Transfer — Drive application ID is 55656082996
        var payload = JsonSerializer.Serialize(new
        {
            oldOwnerUserId = fromId,
            newOwnerUserId = destId,
            applicationDataTransfers = new[]
            {
                new { applicationId = "55656082996" } // Google Drive
            }
        });

        var resp = await client.PostAsync($"{DataTransferBase}/transfers",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("StartDriveTransfer {From}→{To} failed: {Error}", fromEmail, toUserEmail, err);
            return (false, null, $"Data Transfer API error {(int)resp.StatusCode}: {err}");
        }

        using var respDoc  = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var transferId     = respDoc.RootElement.TryGetProperty("id", out var tid) ? tid.GetString() : null;
        return (true, transferId, null);
    }

    // ── Security Audit ────────────────────────────────────────────────────────

    /// <summary>List OAuth 2.0 tokens / authorized apps for a user.</summary>
    public async Task<(bool Success, List<OAuthTokenInfo> Tokens, string? Error)> GetUserTokensAsync(string userEmail)
    {
        var settings = await GetSettingsAsync();
        if (settings == null || string.IsNullOrEmpty(settings.AdminEmail))
            return (false, [], "Admin service account not configured or auth failed.");
        var token = await GetAccessTokenAsync(settings.AdminEmail, SecurityTokensScope);
        if (token == null) return (false, [], "Auth failed — add 'admin.directory.user.security' scope to DWD.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync($"{AdminApiBase}/users/{Uri.EscapeDataString(userEmail)}/tokens");
        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode == 404) return (true, [], null); // user has no tokens
            var err = await resp.Content.ReadAsStringAsync();
            return (false, [], $"API error {(int)resp.StatusCode}: {err}");
        }

        var list = new List<OAuthTokenInfo>();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var displayText  = item.TryGetProperty("displayText",  out var dt)  ? dt.GetString()  ?? "" : "";
                var clientId     = item.TryGetProperty("clientId",     out var cid) ? cid.GetString() ?? "" : "";
                var scopes       = new List<string>();
                if (item.TryGetProperty("scopes", out var sc))
                    foreach (var s in sc.EnumerateArray())
                    {
                        var sv = s.GetString();
                        if (!string.IsNullOrEmpty(sv)) scopes.Add(sv);
                    }
                list.Add(new OAuthTokenInfo(displayText, clientId, scopes));
            }
        }
        return (true, list, null);
    }

    /// <summary>
    /// Fetch recent login events for a user from the Admin Reports Audit API.
    /// </summary>
    public async Task<(bool Success, List<LoginEvent> Events, string? Error)> GetUserLoginActivityAsync(
        string userEmail, int maxResults = 20)
    {
        var settings = await GetSettingsAsync();
        if (settings == null || string.IsNullOrEmpty(settings.AdminEmail))
            return (false, [], "Admin service account not configured or auth failed.");
        var token = await GetAccessTokenAsync(settings.AdminEmail, AuditReportScope);
        if (token == null) return (false, [], "Auth failed — add 'admin.reports.audit.readonly' scope to DWD.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var url  = $"{ReportsApiBase}/activity/users/{Uri.EscapeDataString(userEmail)}/applications/login" +
                   $"?maxResults={maxResults}";
        var resp = await client.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            return (false, [], $"Reports API error {(int)resp.StatusCode}: {err}");
        }

        var list = new List<LoginEvent>();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var time = item.TryGetProperty("id", out var idEl) &&
                           idEl.TryGetProperty("time", out var t) &&
                           DateTimeOffset.TryParse(t.GetString(), out var dto) ? dto : DateTimeOffset.MinValue;

                string eventName = "", ipAddress = "";
                if (item.TryGetProperty("events", out var evts) && evts.GetArrayLength() > 0)
                {
                    var ev = evts[0];
                    eventName = ev.TryGetProperty("name", out var en) ? en.GetString() ?? "" : "";
                    if (ev.TryGetProperty("parameters", out var parms))
                        foreach (var p in parms.EnumerateArray())
                        {
                            var pname = p.TryGetProperty("name", out var pn) ? pn.GetString() : "";
                            if (pname == "ip_address" && p.TryGetProperty("value", out var ip))
                                ipAddress = ip.GetString() ?? "";
                        }
                }

                list.Add(new LoginEvent(time, eventName, ipAddress));
            }
        }
        return (true, list, null);
    }

    /// <summary>
    /// Fetch org-wide user security summary: suspended status, 2FA enrollment, org unit.
    /// Used for the Workspace Security Audit page.
    /// </summary>
    public async Task<(bool Success, List<UserSecurityInfo> Users, string? Error)> GetOrgUsersSecurityAsync()
    {
        var token = await GetAdminAccessTokenAsync();
        if (token == null) return (false, [], "Admin service account not configured or auth failed.");

        var settings = await GetSettingsAsync();
        if (settings == null) return (false, [], "Google Workspace not configured.");

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var list       = new List<UserSecurityInfo>();
        string? pageToken = null;

        do
        {
            var url = $"{AdminApiBase}/users?domain={Uri.EscapeDataString(settings.Domain)}" +
                      "&maxResults=500&projection=full&fields=nextPageToken,users(primaryEmail,name,suspended," +
                      "isEnrolledIn2Sv,isAdmin,orgUnitPath,lastLoginTime,creationTime,changePasswordAtNextLogin)";
            if (pageToken != null) url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

            // Re-create client on each page to avoid header accumulation issues
            using var pageClient = _httpClientFactory.CreateClient();
            pageClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var resp = await pageClient.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                return (false, list, $"API error {(int)resp.StatusCode}: {err}");
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var npt) ? npt.GetString() : null;

            if (!doc.RootElement.TryGetProperty("users", out var users)) break;
            foreach (var u in users.EnumerateArray())
            {
                var email      = u.TryGetProperty("primaryEmail",            out var pe)  ? pe.GetString()  ?? "" : "";
                var fullName   = u.TryGetProperty("name", out var nameEl) &&
                                 nameEl.TryGetProperty("fullName",           out var fn)  ? fn.GetString()  ?? "" : "";
                var suspended  = u.TryGetProperty("suspended",               out var sus) && sus.GetBoolean();
                var has2fa     = u.TryGetProperty("isEnrolledIn2Sv",         out var e2s) && e2s.GetBoolean();
                var isAdmin    = u.TryGetProperty("isAdmin",                 out var adm) && adm.GetBoolean();
                var orgUnit    = u.TryGetProperty("orgUnitPath",             out var ou)  ? ou.GetString()  : null;
                var mustChange = u.TryGetProperty("changePasswordAtNextLogin",out var cp) && cp.GetBoolean();
                DateTimeOffset? lastLogin = null;
                if (u.TryGetProperty("lastLoginTime", out var llt) && DateTimeOffset.TryParse(llt.GetString(), out var lldto))
                    lastLogin = lldto;
                DateTimeOffset? created = null;
                if (u.TryGetProperty("creationTime", out var ct) && DateTimeOffset.TryParse(ct.GetString(), out var ctdto))
                    created = ctdto;

                list.Add(new UserSecurityInfo(email, fullName, suspended, has2fa, isAdmin, orgUnit, lastLogin, created, mustChange));
            }
        } while (pageToken != null);

        return (true, list, null);
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
        var s = await db.GoogleWorkspaceSettings.OrderBy(s => s.Id).FirstOrDefaultAsync();
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

    private static List<GroupInfo> ParseGroups(string json)
    {
        var list = new List<GroupInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("groups", out var arr)) return list;
            foreach (var g in arr.EnumerateArray())
            {
                var email = g.TryGetProperty("email",       out var e)  ? e.GetString() ?? "" : "";
                var name  = g.TryGetProperty("name",        out var n)  ? n.GetString() ?? "" : "";
                var desc  = g.TryGetProperty("description", out var d)  ? d.GetString()       : null;
                var count = g.TryGetProperty("directMembersCount", out var mc)
                    && int.TryParse(mc.GetString(), out var c) ? c : 0;
                list.Add(new GroupInfo(email, name, desc, count));
            }
        }
        catch { /* malformed response */ }
        return list;
    }

    private static string GenerateTempPassword()
    {
        const string upper   = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower   = "abcdefghijkmnpqrstuvwxyz";
        const string digits  = "23456789";
        const string special = "!@#$";
        var all = upper + lower + digits + special;

        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);

        var chars = new char[12];
        chars[0] = upper[bytes[0]   % upper.Length];
        chars[1] = lower[bytes[1]   % lower.Length];
        chars[2] = digits[bytes[2]  % digits.Length];
        chars[3] = special[bytes[3] % special.Length];
        for (int i = 4; i < 12; i++) chars[i] = all[bytes[i] % all.Length];

        // Fisher-Yates shuffle using remaining random bytes
        var rng = new byte[12];
        RandomNumberGenerator.Fill(rng);
        for (int i = 11; i > 0; i--)
        {
            int j = rng[i] % (i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    // ── Inner types ───────────────────────────────────────────────────────────

    public sealed record BulkSignatureResult(string Name, string Email, string Status, string Message);

    public sealed record GoogleUserInfo(
        string Email,
        string FullName,
        bool Suspended,
        bool ChangePasswordAtNextLogin,
        string? OrgUnit
    );

    /// <summary>One user as returned by the Admin Directory list endpoint —
    /// includes the fields needed to seed an Employee record.</summary>
    public sealed record GoogleDirectoryUser(
        string Email,
        string FirstName,
        string LastName,
        string FullName,
        string? JobTitle,
        string? Department,
        string? Phone,
        bool Suspended,
        string? OrgUnit
    );

    public sealed record VacationResponder(
        bool EnableAutoReply,
        string? ResponseSubject,
        string? ResponseBodyHtml,
        DateTimeOffset? StartTime,
        DateTimeOffset? EndTime,
        bool RestrictToContacts,
        bool RestrictToDomain
    );

    public sealed record DriveStorageInfo(
        long GmailUsedMb,
        long DriveUsedMb,
        long TotalQuotaMb,
        DateTime? AsOfDate
    );

    public sealed record GroupInfo(string Email, string Name, string? Description, int MemberCount);

    public sealed record SharedDriveInfo(string Id, string Name);

    public sealed record OAuthTokenInfo(string AppName, string ClientId, List<string> Scopes);

    public sealed record LoginEvent(DateTimeOffset Time, string EventName, string IpAddress);

    public sealed record UserSecurityInfo(
        string Email,
        string FullName,
        bool Suspended,
        bool IsEnrolledIn2Sv,
        bool IsAdmin,
        string? OrgUnit,
        DateTimeOffset? LastLoginTime,
        DateTimeOffset? CreationTime,
        bool MustChangePassword
    );

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
