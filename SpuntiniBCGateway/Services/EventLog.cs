using System.Diagnostics;
using System.Text;
using Microsoft.Identity.Client;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace SpuntiniBCGateway.Services;

/// <summary>
/// EventLog writes messages to Debug output, optionally persists them to a database,
/// and optionally sends an email alert via SMTP when configured.
/// </summary>
public class EventLog(IConfiguration config)
{
    private readonly IConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
    private static readonly SemaphoreSlim _fileLock = new(1, 1);

    public static string GetMethodName([CallerMemberName] string methodName = "")
    {
        return methodName;
    }

    public Task InfoAsync(string? method, string? company, string? message, string? details = null) => LogAsync("INFO", method, company, message, details);
    public Task WarningAsync(string? method, string? company, string? message, string? details = null) => LogAsync("WARN", method, company, message, details);
    public Task ErrorAsync(string? method, string? company, Exception? ex) => LogAsync("ERROR", method, company, ex?.Message, ex?.StackTrace);

    public async Task LogAsync(string? level, string? method, string? company, string? message, string? details = null)
    {
        var timestamp = DateTime.Now;

        company ??= "SYSTEM";

        // 1) Always write to Debug output
        try
        {
            Debug.WriteLine($"[{timestamp:O}] | {level} | {Environment.MachineName}-{AppDomain.CurrentDomain.FriendlyName} | {string.Format("{0,-35}", method)} | {string.Format("{0,-20}", company)} | {message}");
            if (!string.IsNullOrWhiteSpace(details))
                Debug.WriteLine($"Details: {details}");
        }
        catch { /* shouldn't fail - but swallow to avoid breaking flow */ }

        // 2) Optionally write to local file(s)
        try
        {
            if (_config.GetValue("EventLog:EnableFile", false))
            {
                string fileDir = _config["EventLog:FileDirectory"] ?? AppContext.BaseDirectory;
                string prefix = _config["EventLog:FilePrefix"] ?? "EventLog";
                int maxFiles = _config.GetValue<int?>("EventLog:MaxFiles") ?? 100;
                long maxFileSize = _config.GetValue<long?>("EventLog:MaxFileSizeBytes") ?? 10L * 1024 * 1024; // 10 MB

                string entry = BuildFileEntry(timestamp, level, method, company, message, details);
                await WriteToFileAsync(fileDir, prefix, maxFiles, maxFileSize, entry).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"EventLog file write failed: {ex}");
            Console.Error.WriteLine($"EventLog file write failed: {ex}");
        }

        // 3) Optionally send email alerts
        try
        {
            if (_config.GetValue("EventLog:EnableEmail", false) && ShouldSendEmail(level))
            {
                await SendEmailAsync(level, method, company, message, details).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"EventLog email send failed: {ex.Message}");
        }
    }

    private bool ShouldSendEmail(string? level)
    {
        // Default: only send on ERROR unless configured otherwise
        bool sendOnErrorOnly = _config.GetValue("EventLog:SendOnErrorOnly", true);
        if (!sendOnErrorOnly)
            return true;
        return string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFileEntry(DateTime timestamp, string? level, string? method, string? company, string? message, string? details)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{timestamp:O}] {level} | {method}  | {company} | {message}");
        if (!string.IsNullOrWhiteSpace(details))
        {
            sb.AppendLine("Details:");
            sb.AppendLine(details);
        }
        sb.AppendLine(new string('-', 80));
        return sb.ToString();
    }

    private static async Task WriteToFileAsync(string? directory, string? prefix, int maxFiles, long maxFileSizeBytes, string entry)
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            directory ??= AppContext.BaseDirectory;
            prefix ??= "EventLog";

            _ = Directory.CreateDirectory(path: directory);

            string ActiveFile(int idx) => Path.Combine(directory, $"{prefix}.{idx}.txt");

            string activePath = ActiveFile(1);

            // If active file exists and exceeds max size, rotate files
            if (File.Exists(activePath))
            {
                var fi = new FileInfo(activePath);
                if (fi.Length >= maxFileSizeBytes)
                {
                    // Delete oldest if it exists
                    string oldest = ActiveFile(maxFiles);
                    if (File.Exists(oldest))
                    {
                        File.Delete(oldest);
                    }

                    // Shift files: (maxFiles-1)->maxFiles, ... 1->2
                    for (int i = maxFiles - 1; i >= 1; i--)
                    {
                        string src = ActiveFile(i);
                        string dst = ActiveFile(i + 1);
                        if (File.Exists(src))
                        {
                            if (File.Exists(dst)) File.Delete(dst);
                            File.Move(src, dst);
                        }
                    }

                    // activePath is now free (it was moved to .2), create new empty active file
                    using var fs = File.Create(activePath);
                }
            }

            // Append the entry to active file
            await File.AppendAllTextAsync(activePath, entry).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task SendEmailAsync(string? level, string? method, string? company, string? message, string? details)
    {
        try
        {
            // Read configuration. We reuse existing SMTP config keys for From/To but use Graph for transport.
            string from = _config["EventLog:Smtp:From"] ?? string.Empty;
            string to = _config["EventLog:Smtp:To"] ?? string.Empty;
            string bcc = _config["EventLog:Smtp:Bcc"] ?? string.Empty;
            bool saveToSentItems = bool.TryParse(_config["EventLog:Smtp:SaveToSentItems"], out bool res) && res;

            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                Debug.WriteLine("EventLog: Email configuration incomplete (From or To missing). Skipping email.");
                return;
            }

            // Acquire a token for Microsoft Graph using client credentials
            string tenantId = _config["Auth:TenantId"] ?? string.Empty;
            string clientId = _config["Auth:ClientId"] ?? string.Empty;
            string clientSecret = _config["Auth:ClientSecret"] ?? string.Empty;
            string graphScope = _config["EventLog:Graph:Scope"] ?? "https://graph.microsoft.com/.default";

            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                Debug.WriteLine("EventLog: AAD client credentials missing. Cannot send email via Graph.");
                return;
            }

            string authority = $"https://login.microsoftonline.com/{tenantId}";
            var app = ConfidentialClientApplicationBuilder.Create(clientId)
                        .WithClientSecret(clientSecret)
                        .WithAuthority(authority)
                        .Build();

            var result = await app.AcquireTokenForClient([graphScope]).ExecuteAsync().ConfigureAwait(false);
            string token = result.AccessToken;

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Build Graph sendMail payload
            string subject = $"[{level}] SpuntiniBCGateway event {Environment.MachineName}-{AppDomain.CurrentDomain.FriendlyName} {method}";
            string body = BuildEmailBody(level, company, message, details);

            string[] toAddresses = to.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            string[] bccAddresses = string.IsNullOrEmpty(bcc)
                ? Array.Empty<string>()
                : bcc.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var toRecipients = toAddresses.Select(a => new { emailAddress = new { address = a } }).ToArray();
            var bccRecipients = bccAddresses.Select(a => new { emailAddress = new { address = a } }).ToArray();

            var messageObj = new
            {
                message = new
                {
                    subject,
                    body = new { contentType = "Text", content = body },
                    toRecipients,
                    bccRecipients
                },
                saveToSentItems
            };

            string json = JsonSerializer.Serialize(messageObj);

            // POST to /users/{from}/sendMail - with app permissions, this requires Mail.Send application permission
            string encodedFrom = Uri.EscapeDataString(from);
            string endpoint = $"https://graph.microsoft.com/v1.0/users/{encodedFrom}/sendMail";

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await http.PostAsync(endpoint, content).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Debug.WriteLine($"EventLog Graph sendMail failed: {resp.StatusCode} {respBody}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"EventLog email send failed: {ex.Message}");
        }
    }

    private static string BuildEmailBody(string? level, string? company, string? message, string? details)
    {
        return $"Level: {level}\nTime(UTC): {DateTime.UtcNow:O}\nCompany: {company}\nMessage: {message}\n\nDetails:\n{details ?? "(none)"}";
    }
}