using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static class ItemUnitOfMeasureBCRequest
{
    // Attempts to find an item in Business Central by `number`, `no` or `gtin`.
    // If found, performs a PATCH to update the item; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage> UpsertItemUnitOfMeasureAsync(HttpClient client, IConfigurationRoot config, string company, string? itemJson, Dictionary<string, Dictionary<string, string>>? allItemData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        string? itemNo = null;
        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(allItemData);
            ArgumentNullException.ThrowIfNull(company);
            if (string.IsNullOrWhiteSpace(itemJson)) throw new ArgumentException("Either itemJson or bulkJson required", nameof(itemJson));

            string collectionUrl = config[$"Companies:{company}:ItemUnitOfMeasureData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:ItemUnitOfMeasureData:DestinationApiUrl required in config");

            // Parse provided JSON to extract number/no/gtin for existence check
            using var doc = JsonDocument.Parse(itemJson);
            var root = doc.RootElement;
            
            if (root.TryGetProperty("no", out var noProp) && noProp.ValueKind == JsonValueKind.String)
                itemNo = noProp.GetString();

            if (string.IsNullOrEmpty(itemNo)) throw new ArgumentException(" No itemNo found in json", nameof(itemJson));

            Dictionary<string, string>? itemResult = [];

            if (!allItemData.TryGetValue(itemNo, out itemResult) || itemResult is null)
            {
                string escaped = itemNo.Replace("'", "''");
                string filter = $"no eq '{escaped}'";

                string itemUrl = config[$"Companies:{company}:ItemData:DestinationApiUrl"] + "?$filter=" + Uri.EscapeDataString(filter) + "&$expand=defaultDimensions,itemUnitOfMeasures";
                // POSSIBLY NEW ITEM

                itemResult = (await BcRequest.GetBcDataAsync(client, itemUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;
                allItemData[itemNo] = itemResult;
            }

            if (itemResult is null || itemResult.Keys.Count <= 0)
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    ReasonPhrase = $"Item {itemNo} not found in data. No update possible."
                };

            // Only check if tradeUnitOfMeasure is not the same as the salesUnitOfMeasure
            if (itemResult.TryGetValue("tradeUnitOfMeasure", out string? tradeUnitOfMeasure) && itemResult.TryGetValue("salesUnitOfMeasure", out string? salesUnitOfMeasure) &&
            !string.IsNullOrEmpty(tradeUnitOfMeasure) && !string.IsNullOrEmpty(salesUnitOfMeasure) && tradeUnitOfMeasure == salesUnitOfMeasure)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    ReasonPhrase = $"Item {itemNo} no uom's to check."
                };
            }

            if (!itemResult.TryGetValue("itemUnitOfMeasures", out string? uomResult))
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    ReasonPhrase = $"Item {itemNo} no uoms found in data. No update needed."
                };

            var uomList = JsonHelper.GetItemsSafe(uomResult.ToString());

            foreach (var value in uomList)
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    string? existingId = null;
                    string? etag = null;
                    string? code = null;
                    int qtyPerUnitOfMeasure = 0;
                    int qtyRoundingPrecision = 0;

                    foreach (var prop in value.EnumerateObject())
                    {
                        if (prop.Name.Equals("systemId"))
                        {
                            existingId = prop.Value.GetString();
                        }
                        else if (prop.Name.Equals("@odata.etag"))
                        {
                            etag = prop.Value.GetString();
                        }
                        else if (prop.Name.Equals("code"))
                        {
                            code = prop.Value.GetString();
                        }
                        else if (prop.Name.Equals("qtyPerUnitOfMeasure"))
                        {
                            StringHelper.TryParseInt(prop.Value.GetRawText(), CultureInfo.CurrentCulture, out qtyPerUnitOfMeasure);
                        }
                        else if (prop.Name.Equals("qtyRoundingPrecision"))
                        {
                            StringHelper.TryParseInt(prop.Value.GetRawText(), CultureInfo.CurrentCulture, out qtyRoundingPrecision);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(existingId) || string.IsNullOrWhiteSpace(code))
                        continue;

                    int qtyPerUnitOfMeasureTemp = UomHelper.GetBcQtyPerUnitOfMeasure(company, code);
                    int qtyRoundingPrecisionTemp = UomHelper.GetBcQtyRoundingPrecision(company, code);

                    if (qtyPerUnitOfMeasure == qtyPerUnitOfMeasureTemp && qtyRoundingPrecision == qtyRoundingPrecisionTemp)
                        continue;

                    var uom = new Dictionary<string, object>() { { "code", code }, { "qtyPerUnitOfMeasure", qtyPerUnitOfMeasureTemp }, { "qtyRoundingPrecision", qtyRoundingPrecisionTemp } };

                    // Update: PATCH to items({id})
                    string updateUrl = $"{collectionUrl}({existingId})";

                    string json = JsonSerializer.Serialize(uom, new JsonSerializerOptions { WriteIndented = false });

                    await BcRequest.PatchBcDataAsync(client, updateUrl, json, etag ?? "*",
                    $"Item uom {itemNo} {code} updated successfully.", $"Failed to update item uom {itemNo} {code}. Json: {itemJson}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
                }
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                ReasonPhrase = $"Item uoms {itemNo} checked."
            };
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
                await logger.InfoAsync(EventLog.GetMethodName(), company, $"UpsertItemAsync {itemNo} completed in {StringHelper.GetDurationString(stopwatch.Elapsed)}.");
            }
        }
    }

    public static async Task<HttpResponseMessage?> AddItemUnitOfMeasureAsync(HttpClient client, IConfigurationRoot config, string company, Dictionary<string, string>? itemData = null,
    List<string>? uomList = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (itemData is null || itemData.Count == 0)
                throw new ArgumentException("itemData required", nameof(itemData));

            if (uomList is null || uomList.Count == 0)
                throw new ArgumentException("uomList required", nameof(uomList));

            if (!itemData.TryGetValue("no", out var itemNumber) || string.IsNullOrEmpty(itemNumber)) 
                throw new ArgumentException("ItemNumber 'no' not found in itemData", nameof(itemData));

            var itemUnitOfMeasureCodes = JsonSerializer.Deserialize<Dictionary<string, object>[]>(itemData["itemUnitOfMeasures"] ?? "[]");

            var uomsToAddList = new List<string>();

            if (itemUnitOfMeasureCodes != null)
            {
                foreach (var uom in uomList)
                {
                    bool found = false;
                    foreach (var uomEntry in itemUnitOfMeasureCodes.AsEnumerable())
                    {
                        if (uomEntry == null || uomEntry["code"] == null) continue;

                        if (uomEntry["code"].ToString() == uom)
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                        uomsToAddList.Add(uom);
                }
            }
            else
            {
                foreach (string uom in uomList)
                {
                    uomsToAddList.Add(uom);
                }
            }

            foreach (string bcUom in uomsToAddList)
            {
                if (string.IsNullOrWhiteSpace(bcUom))
                    continue;

                // TRY ADD IT IN BC FIRST?
                try
                {
                    string postUomUrl = config[$"Companies:{company}:ItemUnitOfMeasureData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:ItemUnitOfMeasureData:DestinationApiUrl required in config");

                    var postData = new Dictionary<string, object>() { { "itemNo", itemNumber }, { "code", bcUom }, { "qtyPerUnitOfMeasure", UomHelper.GetBcQtyPerUnitOfMeasure(company, bcUom)}, { "qtyRoundingPrecision", UomHelper.GetBcQtyRoundingPrecision(company, bcUom) } };

                    var postUomJson = JsonSerializer.Serialize(postData, new JsonSerializerOptions { WriteIndented = false });

                    string postUrl = $"{postUomUrl}";

                    await BcRequest.PostBcDataAsync(client, postUrl, postUomJson,
                         $"Item Uom {itemNumber} {bcUom} create successfully.", $"Failed to create item uom {itemNumber} {bcUom}. Json: {postUomJson}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
                }
                catch (Exception)
                {
                    if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, new Exception($"Failed to add UoM {bcUom} for item {itemNumber}"));                
                    throw;
                }
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                ReasonPhrase = $"Item uoms {itemData["no"]} checked."
            };
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);

            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            {
                ReasonPhrase = ex.Message
            };
        }
    }
}
