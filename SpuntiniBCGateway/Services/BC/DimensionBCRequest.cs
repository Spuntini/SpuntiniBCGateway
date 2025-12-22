using System.Diagnostics;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static class DimensionBCRequest
{
    public static async Task<string> ProcessItemCogsAsync(HttpClient client, IConfigurationRoot config, string company, Dictionary<string, Dictionary<string, string>>? allItemData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        var stopwatchCogs = Stopwatch.StartNew();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Start processing itemscogs for company '{company}'.");
        var resp = await FillItemCogs(client, config, company, allItemData, logger, authHelper, cancellationToken).ConfigureAwait(false);
        stopwatchCogs.Stop();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Finished processing itemscogs for company '{company}' in {StringHelper.GetDurationString(stopwatchCogs.Elapsed)}.");
        return "OK";
    }

    // Attempts to find an item in Business Central by `number`, `no` or `gtin`.
    // If found, performs a PATCH to update the item; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage?> UpsertDimensionAsync(HttpClient client, IConfigurationRoot config, string company, string? json, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(company);
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Either json or bulkJson required", nameof(json));

            string collectionUrl = config[$"Companies:{company}:DimensionData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:DimensionData:DestinationApiUrl required in config");

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
                    var escaped = no.Replace("'", "''");
                    filter = $"no eq '{escaped}'";
                }
                else if (!string.IsNullOrWhiteSpace(gtin))
                {
                    var escaped = gtin.Replace("'", "''");
                    filter = $"gtin eq '{escaped}'";
                }

                Dictionary<string, string>? result = [];
                bool isPatchRequired = false;

                if (!string.IsNullOrWhiteSpace(filter))
                {
                    var getUrl = collectionUrl + "?$filter=" + Uri.EscapeDataString(filter);

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
                        var updateUrl = $"{collectionUrl}({existingId})";

                        // Remove excluded fields from JSON before PATCH
                        var excludedFields = config.GetSection($"Companies:{company}:DimensionData:FieldsToExcludeFromUpdate").Get<string[]>();

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

    public static async Task<HttpResponseMessage?> FillItemCogs(HttpClient client, IConfigurationRoot config, string company, Dictionary<string, Dictionary<string, string>>? allItemData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(company);
            ArgumentNullException.ThrowIfNull(allItemData);

            bool useDefaultDimensions = bool.TryParse(config[$"Companies:{company}:ItemData:UseDefaultDimension"], out var dimensions) ? dimensions : false;

            if (!useDefaultDimensions)
            {
                if (logger != null)
                {
                    await logger.InfoAsync(EventLog.GetMethodName(), company, $"UpsertItemAsync completed in {StringHelper.GetDurationString(stopwatch.Elapsed)}.");
                }
                return null;
            }

            string collectionUrl = config[$"Companies:{company}:ItemData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:ItemData:DestinationApiUrl required in config");
            string dimensionCollectionUrl = config[$"Companies:{company}:DimensionData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:DimensionData:DestinationApiUrl required in config");

            string? systemId = null;
            string? no = null;
            string? defaultDimensions = null;
            HttpResponseMessage? responseMessage = null;

            if (allItemData != null)
            {
                foreach (var itemResult in allItemData.Values)
                {
                    systemId = null;
                    no = null;
                    defaultDimensions = null;

                    // DIMENSION MAY NOT EXIST!
                    itemResult.TryGetValue("defaultDimensions", out defaultDimensions);
                    if (!(defaultDimensions is null || string.IsNullOrWhiteSpace(defaultDimensions) || "[]".Equals(defaultDimensions, StringComparison.InvariantCultureIgnoreCase)))
                        continue;

                    itemResult.TryGetValue("systemId", out systemId);
                    itemResult.TryGetValue("no", out no);

                    if (string.IsNullOrWhiteSpace(no)) continue;

                    var dimension = GetItemCogsDimension(config, company, no);

                    var json = JsonSerializer.Serialize(dimension, new JsonSerializerOptions { WriteIndented = false });

                    if (!string.IsNullOrWhiteSpace(systemId))
                    {
                        responseMessage = await BcRequest.PostBcDataAsync(client, dimensionCollectionUrl, json,
                            $"Dimension created for item {no} successfully.", $"Failed to create dimensions for item {no}. Json: {json}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
                    }
                }
            }

            return responseMessage;
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

    internal static Dictionary<string, object> GetItemCogsDimension(IConfiguration config, string company, string itemCode)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(company);
        ArgumentException.ThrowIfNullOrEmpty(itemCode);

        string? dimensionCode = config[$"Companies:{company}:ItemData:DefaultDimensions:DimensionCode"];
        if (string.IsNullOrWhiteSpace(dimensionCode)) dimensionCode = "AFDELING";
        string? dimensionValueCode = config[$"Companies:{company}:ItemData:DefaultDimensions:DimensionValueCode"];
        if (string.IsNullOrWhiteSpace(dimensionValueCode)) dimensionValueCode = "COGS";
        string? valuePosting = config[$"Companies:{company}:ItemData:DefaultDimensions:ValuePosting"];
        if (string.IsNullOrWhiteSpace(valuePosting)) valuePosting = "Code_x0020_Mandatory";

        if (!int.TryParse(config[$"Companies:{company}:ItemData:DefaultDimensions:TableID"], out var tableId))
            tableId = 27;

        return new Dictionary<string, object>
                    {
                        {"dimensionCode", dimensionCode},
                        {"dimensionValueCode", dimensionValueCode},
                        {"no", itemCode},
                        {"tableID", tableId},
                        {"valuePosting", valuePosting},
                    };
    }
}
