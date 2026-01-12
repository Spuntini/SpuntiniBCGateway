using System.Diagnostics;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static class PriceListLinesBCRequest
{
    // Attempts to find an item in Business Central by `number`, `no` or `gtin`.
    // If found, performs a PATCH to update the item; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage?> UpsertPriceListLinesAsync(HttpClient client, IConfigurationRoot config, string company, string? json, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(company);
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Json required", nameof(json));

            string collectionUrl = config[$"Companies:{company}:PriceListLines:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:PriceListLines:DestinationApiUrl required in config");

            string? existingId = null;
            string? etag = null;

            if (!string.IsNullOrWhiteSpace(json))
            {
                // Parse provided JSON to extract number/no/gtin for existence check
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string? no = null;
                string? gtin = null;

                if (root.TryGetProperty("no", out var noProp) && noProp.ValueKind == JsonValueKind.String)
                    no = noProp.GetString();
                if (root.TryGetProperty("gtin", out var gtinProp) && gtinProp.ValueKind == JsonValueKind.String)
                    gtin = gtinProp.GetString();

                // Build filter expression: prefer number, then no, then gtin
                string? filter = null;
                if (!string.IsNullOrWhiteSpace(no))
                {
                    string escaped = no.Replace("'", "''");
                    filter = $"no eq '{escaped}'";
                }
                else if (!string.IsNullOrWhiteSpace(gtin))
                {
                    string escaped = gtin.Replace("'", "''");
                    filter = $"gtin eq '{escaped}'";
                }

                Dictionary<string, string>? result = [];
                bool isPatchRequired = false;

                if (!string.IsNullOrWhiteSpace(filter))
                {
                    string getUrl = collectionUrl + "?$select=systemId&$filter=" + filter;

                    result = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

                    if (result != null)
                    {
                        result.TryGetValue("systemId", out existingId);
                        result.TryGetValue("@odata.etag", out etag);
                    }

                    if (!string.IsNullOrWhiteSpace(existingId))
                    {
                        // Check if PATCH is required by comparing fields
                        isPatchRequired = await JsonHelper.IsPatchRequiredAsync(result, json, ["skipDuplicateCheck"], null, logger, company);
                    }
                }

                if (!string.IsNullOrWhiteSpace(existingId))
                {
                    if (isPatchRequired)
                    {
                        // Update: PATCH to items({id})
                        string updateUrl = $"{collectionUrl}({existingId})";

                        // Remove excluded fields from JSON before PATCH
                        string[]? excludedFields = config.GetSection($"Companies:{company}:DimensionData:ExcludedFieldsForPatch").Get<string[]>();

                        json = await JsonHelper.RemoveFieldsFromJsonAsync(json, excludedFields, logger, company);

                        return await BcRequest.PatchBcDataAsync(client, updateUrl, json, etag ?? "*",
                        $"Item {no} updated successfully.", $"Failed to update item {no}. Json: {json}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
                    }

                    stopwatch.Stop();
                    if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Duration: {StringHelper.GetDurationString(stopwatch.Elapsed)}. Item {no} already exists. No update required.");
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        ReasonPhrase = $"Item {no} already exists. No update required."
                    };
                }
                else
                {
                    return await BcRequest.PostBcDataAsync(client, collectionUrl, json,
                    $"Item {no} created successfully.", $"Failed to create item {no}. Json: {json}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);

            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            {
                ReasonPhrase = ex.Message
            };
        }
        finally
        {
            stopwatch.Stop();
            if (logger != null)
            {
                await logger.InfoAsync(EventLog.GetMethodName(), company, $"UpsertItemAsync completed in {StringHelper.GetDurationString(stopwatch.Elapsed)}.");
            }
        }
    }

    public static async Task<HttpResponseMessage?> DeletePriceListLinesAsync(HttpClient client, IConfigurationRoot config, string company, string? json, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Json required", nameof(json));

            string collectionUrl = config[$"Companies:{company}:PriceListLines:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:PriceListLines:DestinationApiUrl required in config");

            string? existingId = null;
            string? etag = null;

            if (!string.IsNullOrWhiteSpace(json))
            {
                // Parse provided JSON to extract number/no/gtin for existence check
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string? no = null;
                string? gtin = null;

                if (root.TryGetProperty("no", out var noProp) && noProp.ValueKind == JsonValueKind.String)
                    no = noProp.GetString();
                if (root.TryGetProperty("gtin", out var gtinProp) && gtinProp.ValueKind == JsonValueKind.String)
                    gtin = gtinProp.GetString();

                // Build filter expression: prefer number, then no, then gtin
                string? filter = null;
                if (!string.IsNullOrWhiteSpace(no))
                {
                    string escaped = no.Replace("'", "''");
                    filter = $"no eq '{escaped}'";
                }
                else if (!string.IsNullOrWhiteSpace(gtin))
                {
                    string escaped = gtin.Replace("'", "''");
                    filter = $"gtin eq '{escaped}'";
                }

                Dictionary<string, string>? result = [];

                if (!string.IsNullOrWhiteSpace(filter))
                {
                    string getUrl = collectionUrl + "?$filter=" + filter;

                    result = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

                    if (result != null)
                    {
                        result.TryGetValue("systemId", out existingId);
                        result.TryGetValue("@odata.etag", out etag);
                    }

                    if (string.IsNullOrWhiteSpace(existingId))
                    {
                        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, "Delete not possible, price list line not found");                
                        return null;
                    }
                }

                return await BcRequest.DeleteBcDataAsync(client, collectionUrl, json, etag ?? "*",
                        $"Price list line deleted successfully.", $"Failed to delete pricelist line. Json: {json}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);                     
            }   

            return null;             
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);

            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            {
                ReasonPhrase = ex.Message
            };
        }
        finally
        {
            stopwatch.Stop();
            if (logger != null)
            {
                await logger.InfoAsync(EventLog.GetMethodName(), company, $"UpsertItemAsync completed in {StringHelper.GetDurationString(stopwatch.Elapsed)}.");
            }
        }
    }
}
