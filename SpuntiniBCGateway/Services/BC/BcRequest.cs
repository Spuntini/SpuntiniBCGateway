using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static class BcRequest
{
    // Tries to get a value from the dictionary by key, ignoring case.
    public static async Task<Dictionary<string, Dictionary<string, string>>> GetBcDataAsync(HttpClient client, string getUrl,
        string keyDefinition, string sourceMethod, EventLog? logger = null, string company = "", AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"{sourceMethod} => GetBcDataAsync start").ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(keyDefinition)) throw new ArgumentNullException("Key Definition is null");

            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            string? nextUrl = getUrl;

            while (!string.IsNullOrWhiteSpace(nextUrl))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
                var getResp = await SendAsync(client, request, "", "Failed", $"{sourceMethod}: GET", logger, company, authHelper, cancellationToken).ConfigureAwait(false);

                if (!getResp.IsSuccessStatusCode)
                {
                    // SendAsync already logs and throws on non-success; break defensively
                    break;
                }

                string content = await getResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    using var respDoc = JsonDocument.Parse(content);
                    var root = respDoc.RootElement;

                    var tempResult = JsonHelper.GetDataFromJsonElement(root, keyDefinition);
                    if (tempResult.Any())
                        result = result.Concat(tempResult).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                    // check for @odata.nextLink for pagination
                    if (root.TryGetProperty("@odata.nextLink", out var nextProp) && nextProp.ValueKind == JsonValueKind.String)
                    {
                        nextUrl = nextProp.GetString();
                    }
                    else
                    {
                        nextUrl = null;
                    }
                }
                catch (Exception ex)
                {
                    if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex).ConfigureAwait(false);
                    break;
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex).ConfigureAwait(false);
            return [];
        }
        finally
        {
            stopwatch.Stop();
            if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"{sourceMethod} => GetBcDataAsync completed in {StringHelper.GetDurationString(stopwatch.Elapsed)}.").ConfigureAwait(false);
        }
    }

    public static async Task<HttpResponseMessage> PostBcDataAsync(HttpClient client, string postUrl, string json, string succesMessage = "Created successfully", string errorMessage = "Creation failed", string sourceMethod = "", string? accept = null, EventLog? logger = null, string company = "", AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
                return await SendAsync(client, new HttpRequestMessage(HttpMethod.Post, postUrl), succesMessage, errorMessage, $"{sourceMethod}: POST", logger, company, authHelper, cancellationToken);

            // Create: POST to collection
            using var request = new HttpRequestMessage(HttpMethod.Post, postUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(accept))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            }

            return await SendAsync(client, request, succesMessage, errorMessage, $"{sourceMethod}: POST", logger, company, authHelper, cancellationToken);
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { ReasonPhrase = ex.Message };
        }
    }

    public static async Task<HttpResponseMessage> PatchBcDataAsync(HttpClient client, string patchUrl, string getUrl, string keyValue, string? json, string etag, string succesMessage = "Patch successfully", string errorMessage = "Patch failed", string sourceMethod = "", EventLog? logger = null, string company = "", AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentException.ThrowIfNullOrEmpty(json);

            var maxRetryAttempts = 5;
            var retryAttempts = 0;

            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Patch, patchUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    Headers = { { "If-Match", etag ?? "*" }, { "Prefer", "return=representation" } }
                };

                var responseMessage = await SendAsync(client, request, succesMessage, errorMessage, $"{sourceMethod}: PATCH", logger, company, authHelper, cancellationToken);

                if (responseMessage.StatusCode == System.Net.HttpStatusCode.Conflict &&
                   responseMessage.RequestMessage != null &&
                    responseMessage.RequestMessage.Method == HttpMethod.Patch)
                {
                    retryAttempts++;
                    if (retryAttempts > maxRetryAttempts) return responseMessage;
                   
                    var result = (await GetBcDataAsync(client, getUrl, keyValue, EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

                    _ = result.TryGetValue("@odata.etag", out etag);     
                }
                else
                {
                    return responseMessage;
                }
            }
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { ReasonPhrase = ex.Message };
        }
    }

    public static async Task<HttpResponseMessage> DeleteBcDataAsync(HttpClient client, string deleteUrl, string? json, string etag,
        string succesMessage = "Delete successfully", string errorMessage = "Delete failed", string sourceMethod = "", EventLog? logger = null, string company = "", AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentException.ThrowIfNullOrEmpty(json);

            using var request = new HttpRequestMessage(HttpMethod.Delete, deleteUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                Headers = { { "If-Match", etag ?? "*" }, { "Prefer", "return=representation" } }
            };

            return await SendAsync(client, request, succesMessage, errorMessage, $"{sourceMethod}: DELETE", logger, company, authHelper, cancellationToken);
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { ReasonPhrase = ex.Message };
        }
    }

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, string? succesMessage = null,
        string errorMessage = "Error occured", string sourceMethod = "", EventLog? logger = null, string company = "", AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var maxRetryAttempts = 5;
        var retryAttempts = 0;

        try
        {
            while (true)
            {
                // Clone the request for retries to avoid "request body already consumed" issues
                HttpRequestMessage requestToSend = request;
                if (retryAttempts > 0)
                {
                    requestToSend = await CloneRequestAsync(request).ConfigureAwait(false);
                }

                HttpResponseMessage responseMessage = await client.SendAsync(requestToSend, cancellationToken).ConfigureAwait(false);

                if (responseMessage.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    if (authHelper != null)
                    {
                        string token = await authHelper.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

                        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                        // Clone request again for the retry after token refresh
                        requestToSend = await CloneRequestAsync(request).ConfigureAwait(false);
                        responseMessage = await client.SendAsync(requestToSend, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (responseMessage.StatusCode == System.Net.HttpStatusCode.Conflict &&
                    responseMessage.RequestMessage != null &&
                    responseMessage.RequestMessage.Method == HttpMethod.Patch)
                {
                    // For PATCH requests, a 409 Conflict may indicate an etag mismatch
                    string content = await responseMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (content.Contains("Request_EntityChanged", StringComparison.OrdinalIgnoreCase))
                    {
                        return responseMessage;
                    }
                }

                if (responseMessage.StatusCode == System.Net.HttpStatusCode.Conflict ||
                    responseMessage.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    retryAttempts++;
                    if (retryAttempts <= maxRetryAttempts)
                    {
                        if (logger != null) await logger.WarningAsync(EventLog.GetMethodName(), company, $"{sourceMethod} => Attempt {retryAttempts} received {responseMessage.StatusCode}. Retrying...").ConfigureAwait(false);
                        await Task.Delay(1000 * retryAttempts, cancellationToken).ConfigureAwait(false); // Exponential backoff
                        continue;
                    }

                    string content = await responseMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    throw new Exception($"{sourceMethod} => {errorMessage} - StatusCode: {responseMessage.StatusCode}, Content: {content}");
                }

                if (!responseMessage.IsSuccessStatusCode)
                {
                    string content = await responseMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    throw new Exception($"{sourceMethod} => {errorMessage} - StatusCode: {responseMessage.StatusCode}, Content: {content}");
                }

                if (!string.IsNullOrWhiteSpace(succesMessage))
                {
                    stopwatch.Stop();
                    if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"{sourceMethod} => {succesMessage} in {StringHelper.GetDurationString(stopwatch.Elapsed)}.");
                }

                return responseMessage;
            }
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { ReasonPhrase = ex.Message };
        }
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version
        };

        // Copy headers
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Clone content if it exists
        if (request.Content != null)
        {
            var ms = new MemoryStream();
            await request.Content.CopyToAsync(ms).ConfigureAwait(false);
            ms.Position = 0;

            var streamContent = new StreamContent(ms);
            if (request.Content.Headers.ContentType != null)
            {
                streamContent.Headers.ContentType = request.Content.Headers.ContentType;
            }

            // Copy other content headers
            foreach (var header in request.Content.Headers)
            {
                if (header.Key != "Content-Type")
                {
                    streamContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            clone.Content = streamContent;
        }

        return clone;
    }
}