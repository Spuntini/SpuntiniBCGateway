using System.Diagnostics;
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

                    // process 'value' array
                    if (root.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var value in val.EnumerateArray())
                        {
                            string? resultKey = null;
                            
                            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(keyDefinition, out JsonElement keyProp))
                            {
                                resultKey = keyProp.ValueKind == JsonValueKind.String ? keyProp.GetString() : keyProp.GetRawText();
                            }

                            if (string.IsNullOrWhiteSpace(resultKey)) continue;

                            var tempResult = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                            if (value.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var prop in value.EnumerateObject())
                                {
                                    if (prop.Value.ValueKind == JsonValueKind.String)
                                    {
                                        string? tempVal = prop.Value.GetString();
                                        if (string.IsNullOrWhiteSpace(tempVal)) tempVal = "";
                                        tempResult[prop.Name] = tempVal;
                                    }
                                    else
                                    {
                                        tempResult[prop.Name] = prop.Value.GetRawText();
                                    }
                                }
                            }

                            // Add or overwrite existing key from previous pages
                            result[resultKey] = tempResult;
                        }
                    }

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

    public static async Task<HttpResponseMessage> PostBcDataAsync(HttpClient client, string postUrl, string json, string succesMessage = "Created successfully", string errorMessage = "Creation failed", string sourceMethod = "", EventLog? logger = null, string company = "", AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
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

            return await SendAsync(client, request, succesMessage, errorMessage, $"{sourceMethod}: POST" , logger, company, authHelper, cancellationToken); 
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { ReasonPhrase = ex.Message };
        }
    }

    public static async Task<HttpResponseMessage> PatchBcDataAsync(HttpClient client, string patchUrl, string? json, string etag, string succesMessage = "Patch successfully", string errorMessage = "Patch failed", string sourceMethod = "", EventLog? logger = null, string company = "", AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {       
        try
        {
            ArgumentException.ThrowIfNullOrEmpty(json);
            
            using var request = new HttpRequestMessage(new HttpMethod("PATCH"), patchUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                Headers = { { "If-Match", etag ?? "*" }, { "Prefer", "return=representation" } }
            };

            return await SendAsync(client, request, succesMessage, errorMessage, $"{sourceMethod}: PATCH" , logger, company, authHelper, cancellationToken); 
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
            
            using var request = new HttpRequestMessage(new HttpMethod("DELETE"), deleteUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                Headers = { { "If-Match", etag ?? "*" }, { "Prefer", "return=representation" } }
            };

            return await SendAsync(client, request, succesMessage, errorMessage, $"{sourceMethod}: DELETE" , logger, company, authHelper, cancellationToken);            
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { ReasonPhrase = ex.Message };
        }
    }   

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, string? succesMessage = null, 
        string errorMessage = "Error occured", string sourceMethod = "", EventLog? logger = null, string company = "", AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            HttpResponseMessage responseMessage = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (responseMessage.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                if (authHelper != null)
                {
                    string token = await authHelper.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                    responseMessage = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
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
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { ReasonPhrase = ex.Message };
        }
    }
}