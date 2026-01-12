using System.Diagnostics;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static class ItemBCRequest
{
    public static async Task<Dictionary<string, Dictionary<string, string>>> GetItemsAsync(HttpClient client, IConfigurationRoot config, string? company = null, string filter = "", string expand = "", EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(company);

        string itemUrl = config[$"Companies:{company}:ItemData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:ItemData:DestinationApiUrl required in config");
        bool useDefaultDimensions = bool.TryParse(config[$"Companies:{company}:ItemData:UseDefaultDimensions"], out bool dimensions) ? dimensions : false;

        if (string.IsNullOrWhiteSpace(filter))
            filter = config[$"Companies:{company}:ItemData:SelectAllFilter"] ?? "";

        if (!filter.Contains("?$filter="))
            filter = "?$filter=" + filter;

        if (string.IsNullOrWhiteSpace(expand))
            expand = useDefaultDimensions ? "$expand=defaultDimensions,itemUnitOfMeasures,itemReferences" : "$expand=itemUnitOfMeasures,itemReferences";
        else if (!expand.Contains("$expand="))
            expand = "$expand=" + expand;

        if (string.IsNullOrWhiteSpace(filter))
        {
            return await BcRequest.GetBcDataAsync(client, $"{itemUrl}?{expand}", "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
        }

        return await BcRequest.GetBcDataAsync(client, $"{itemUrl}{filter}&{expand}", "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
    }

    // Attempts to find an item in Business Central by `number`, `no` or `gtin`.
    // If found, performs a PATCH to update the item; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage?> UpsertItemAsync(HttpClient client, IConfigurationRoot config, string company, string? itemJson, Dictionary<string, Dictionary<string, string>>? allItemData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        string? no = null;

        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            if (string.IsNullOrWhiteSpace(itemJson)) throw new ArgumentException("Either itemJson or bulkJson required", nameof(itemJson));

            string collectionUrl = config[$"Companies:{company}:ItemData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:ItemData:DestinationApiUrl required in config");

            bool useDefaultDimensions = bool.TryParse(config[$"Companies:{company}:ItemData:UseDefaultDimensions"], out bool dimensions) ? dimensions : false;

            string? existingId = null;
            string? etag = null;

            // Parse provided JSON to extract number/no/gtin for existence check
            using var doc = JsonDocument.Parse(itemJson);
            var root = doc.RootElement;

            string? gtin = null;
            string? tradeUnitOfMeasureCode = null;
            string? baseUnitOfMeasureCode = null;

            if (root.TryGetProperty("no", out var noProp) && noProp.ValueKind == JsonValueKind.String)
                no = noProp.GetString();
            if (root.TryGetProperty("gtin", out var gtinProp) && gtinProp.ValueKind == JsonValueKind.String)
                gtin = gtinProp.GetString();
            if (root.TryGetProperty("tradeUnitOfMeasure", out var uomProp) && uomProp.ValueKind == JsonValueKind.String)
                tradeUnitOfMeasureCode = uomProp.GetString();
            if (root.TryGetProperty("baseUnitOfMeasure", out var uomBaseProp) && uomBaseProp.ValueKind == JsonValueKind.String)
                baseUnitOfMeasureCode = uomBaseProp.GetString();

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

            if (string.IsNullOrWhiteSpace(filter))
                throw new ArgumentException($"No filter found");

            Dictionary<string, string>? itemResult = [];
            bool isPatchRequired = false;

            string getUrl = collectionUrl + "?$filter=" + Uri.EscapeDataString(filter) + (useDefaultDimensions ? "&$expand=defaultDimensions,itemUnitOfMeasures,itemReferences" : "&$expand=itemUnitOfMeasures,itemReferences");

            if (allItemData == null || string.IsNullOrWhiteSpace(no) || !allItemData.TryGetValue(no, out itemResult))
            {
                itemResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

                if (allItemData != null && !string.IsNullOrWhiteSpace(no) && itemResult != null)
                {
                    allItemData ??= [];
                    allItemData[no] = itemResult;                    
                }
            }

            string? currentTradeUom = string.Empty;

            if (itemResult != null)
            {
                itemResult.TryGetValue("systemId", out existingId);
                itemResult.TryGetValue("@odata.etag", out etag);
                itemResult.TryGetValue("tradeUnitOfMeasure", out currentTradeUom);
            }

            if (!string.IsNullOrWhiteSpace(existingId))
            {
                // THERE ARE DOUBLE LINES IN THE FILE WITH DIFFERENT UOMS, HANDLE THIS CASE ==> First one wins
                if (!string.IsNullOrWhiteSpace(currentTradeUom) && currentTradeUom != tradeUnitOfMeasureCode)
                    tradeUnitOfMeasureCode = currentTradeUom;

                if (!"STUKS".Equals(tradeUnitOfMeasureCode) && !string.IsNullOrWhiteSpace(tradeUnitOfMeasureCode))
                {
                    itemJson = JsonHelper.ReplacePathValues(itemJson,
                        new Dictionary<string, object> {{"baseUnitOfMeasure", "STUKS"},
                            {"salesUnitOfMeasure", "STUKS"},
                            {"purchUnitOfMeasure", "STUKS"},
                            {"tradeUnitOfMeasure", tradeUnitOfMeasureCode}}, true);
                }

                // Check if PATCH is required by comparing fields
                isPatchRequired = await JsonHelper.IsPatchRequiredAsync(itemResult, itemJson, ["skipDuplicateCheck", "defaultDimensions", "itemReferences", "itemUnitOfMeasures"],
                    new Dictionary<string, List<string>> { { "showInCompany", new List<string> { "SPBI", "SPBS" } } }, logger, company);
            }

            root.TryGetProperty("itemUnitOfMeasures", out var itemUnitOfMeasures);
            root.TryGetProperty("itemReferences", out var itemReferences);

            bool uomsAlreadyUpdated = false;
            bool itemReferencesAlreadyUpdated = false;
            if (string.IsNullOrWhiteSpace(existingId))
            {
                string postUrl = collectionUrl;

                if (useDefaultDimensions)
                    postUrl += "?$expand=defaultDimensions";

                HttpResponseMessage? result = null;
                bool update = false;

                if (itemReferences.ValueKind == JsonValueKind.Array || itemUnitOfMeasures.ValueKind == JsonValueKind.Array)
                {
                    string? tempJson = (string)itemJson.Clone();
                    tempJson = await JsonHelper.RemoveFieldsFromJsonAsync(tempJson, ["itemUnitOfMeasures", "itemReferences"], logger, company);
                    if (!"STUKS".Equals(tradeUnitOfMeasureCode))
                    {
                        tempJson = JsonHelper.ReplacePathValues(tempJson, new Dictionary<string, object>() { { "tradeUnitOfMeasure", "STUKS" } }, false);
                    }

                    result = await BcRequest.PostBcDataAsync(client, postUrl, tempJson, $"Item {no} created successfully.", $"Failed to create item {no}. Json: {tempJson}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
                    update = true;
                }
                else
                {
                    result = await BcRequest.PostBcDataAsync(client, postUrl, itemJson, $"Item {no} created successfully.", $"Failed to create item {no}. Json: {itemJson}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
                }

                if (!update)
                    return result;

                itemResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

                if (itemResult == null)
                    ArgumentNullException.ThrowIfNull(itemResult, $"Failed to retrieve item {no} after creation.");

                allItemData ??= [];
                
                if (allItemData != null && !string.IsNullOrWhiteSpace(no))
                    allItemData[no] = itemResult;

                itemResult.TryGetValue("systemId", out existingId);
                itemResult.TryGetValue("@odata.etag", out etag);

                if (itemUnitOfMeasures.ValueKind == JsonValueKind.Array)
                    await ItemUnitOfMeasureBCRequest.UpsertItemUnitOfMeasureAsync(client, config, company, itemJson, allItemData, logger, authHelper, cancellationToken);

                if (itemReferences.ValueKind == JsonValueKind.Array)
                    await ItemReferencesBCRequest.UpsertItemReferencesAsync(client, config, company, itemJson, allItemData, logger, authHelper, cancellationToken);

                isPatchRequired = true;
            }
            else
            {
                if (itemUnitOfMeasures.ValueKind == JsonValueKind.Array)
                {
                    // Update Uom's first!
                    // get data from json
                    var uomData = JsonHelper.GetDataFromJsonElement(itemUnitOfMeasures, "code");
                    if (uomData != null && uomData.Count != 0)
                    {
                        await ItemUnitOfMeasureBCRequest.UpsertItemUnitOfMeasureAsync(client, config, company, itemJson, allItemData, logger, authHelper, cancellationToken);
                        uomsAlreadyUpdated = true;
                    }
                }

                if (itemReferences.ValueKind == JsonValueKind.Array)
                {
                    // Update Uom's first!
                    // get data from json
                    var itemReferenceData = JsonHelper.GetDataFromJsonElement(itemReferences, "referenceNo");
                    if (itemReferenceData != null && itemReferenceData.Count != 0)
                    {
                        await ItemReferencesBCRequest.UpsertItemReferencesAsync(client, config, company, itemJson, allItemData, logger, authHelper, cancellationToken);
                        itemReferencesAlreadyUpdated = true;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(existingId) && isPatchRequired)
            {
                // Update: PATCH to items({id})
                string updateUrl = $"{collectionUrl}({existingId})";

                // Remove excluded fields from JSON before PATCH
                string[]? excludedFields = config.GetSection($"Companies:{company}:ItemData:FieldsToExcludeFromUpdate").Get<string[]>();

                itemJson = await JsonHelper.RemoveFieldsFromJsonAsync(itemJson, excludedFields, logger, company);

                await BcRequest.PatchBcDataAsync(client, updateUrl, itemJson, etag ?? "*",
                 $"Item {no} updated successfully.", $"Failed to update item {no}. 1 Json: {itemJson}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);

                itemResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;
            }

            if (uomsAlreadyUpdated || itemReferencesAlreadyUpdated) return new HttpResponseMessage(System.Net.HttpStatusCode.OK);

            if (allItemData == null && string.IsNullOrWhiteSpace(no) == false && itemResult != null)
                allItemData = new Dictionary<string, Dictionary<string, string>> { { no, itemResult } };

            if (allItemData == null) return new HttpResponseMessage(System.Net.HttpStatusCode.OK);

            // CHECK IF UOMS OR CORRECTLY SET-UP
            return await ItemUnitOfMeasureBCRequest.UpsertItemUnitOfMeasureAsync(client, config, company, itemJson, allItemData, logger, authHelper, cancellationToken);
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
                await logger.InfoAsync(EventLog.GetMethodName(), company, $"UpsertItemAsync {no} completed in {StringHelper.GetDurationString(stopwatch.Elapsed)}.");
            }
        }
    }
}
