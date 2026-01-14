using System.Diagnostics;
using System.Text.Json;
using DocumentFormat.OpenXml.Drawing.Diagrams;

namespace SpuntiniBCGateway.Services;

public static class ItemReferencesBCRequest
{
    public const string ReferenceType_Vendor = "Vendor";
    public const string ReferenceType_BarCode = "Bar_x0020_Code";

    // Attempts to find an item in Business Central by `number`, `no` or `gtin`.
    // If found, performs a PATCH to update the item; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage> UpsertItemReferencesAsync(HttpClient client, IConfigurationRoot config, string company, string? itemJson, Dictionary<string, Dictionary<string, string>>? allItemData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
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

            string collectionUrl = config[$"Companies:{company}:ItemReferenceData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:ItemReferenceData:DestinationApiUrl required in config");

            // Parse provided JSON to extract number/no/gtin for existence check
            using var doc = JsonDocument.Parse(itemJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("no", out var noProp) && noProp.ValueKind == JsonValueKind.String)
                itemNo = noProp.GetString();

            if (string.IsNullOrEmpty(itemNo)) throw new ArgumentException("No itemNo found in json", nameof(itemJson));

            if (!root.TryGetProperty("itemReferences", out var itemReferences) || noProp.ValueKind == JsonValueKind.Array)
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    ReasonPhrase = $"Item {itemNo} json doesn't contain item reference data"
                };

            Dictionary<string, string>? itemResult = [];

            string escaped = itemNo.Replace("'", "''");
            string filter = $"no eq '{escaped}'";

            string getUrl = config[$"Companies:{company}:ItemData:DestinationApiUrl"] + "?$filter=" + filter + "&$expand=defaultDimensions,itemUnitOfMeasures,itemReferences";

            if (!allItemData.TryGetValue(itemNo, out itemResult) || itemResult is null)
            {
                // POSSIBLY NEW ITEM
                itemResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;
                allItemData[itemNo] = itemResult;
            }

            if (itemResult is null || itemResult.Keys.Count <= 0)
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    ReasonPhrase = $"Item {itemNo} not found in data. No update possible. Item must be posted first"
                };

            var itemReferencesList = JsonHelper.GetItemsSafe(itemReferences.ToString());

            Dictionary<string, Dictionary<string, string>>? currentItemReferenceData = null;
            string[]? excludedFields = config.GetSection($"Companies:{company}:ItemReferenceData:FieldsToExcludeFromUpdate").Get<string[]>();
            string fieldToUpdate = string.Empty;

            if (itemResult.TryGetValue("itemReferences", out var currentItemReferenceList))
                currentItemReferenceData = JsonHelper.GetDataFromJsonString(currentItemReferenceList, "systemId");

            foreach (var value in itemReferencesList)
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    string? existingId = null;
                    string? etag = null;
                    string? referenceType = null;
                    string? referenceTypeNo = null;
                    string? referenceNo = null;
                    string? unitOfMeasure = null;

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
                        else if (prop.Name.Equals("referenceType"))
                        {
                            referenceType = prop.Value.GetString();
                        }
                        else if (prop.Name.Equals("referenceTypeNo"))
                        {
                            referenceTypeNo = prop.Value.GetString();
                        }
                        else if (prop.Name.Equals("referenceNo"))
                        {
                            referenceNo = prop.Value.GetString();
                        }
                        else if (prop.Name.Equals("unitOfMeasure"))
                        {
                            unitOfMeasure = prop.Value.GetString();
                        }
                    }

                    if (string.IsNullOrWhiteSpace(referenceNo) || string.IsNullOrWhiteSpace(referenceType) || string.IsNullOrWhiteSpace(unitOfMeasure))
                        continue;

                    var obj = new Dictionary<string, object>
                            {
                                {"itemNo", itemNo},
                                {"referenceType", referenceType},
                                {"referenceTypeNo", referenceTypeNo ?? ""},
                                {"referenceNo", referenceNo},
                                {"unitOfMeasure", unitOfMeasure}
                            };

                    string json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = false });

                    if (!string.IsNullOrWhiteSpace(existingId) && currentItemReferenceData != null && currentItemReferenceData.TryGetValue(existingId, out var currentData))
                    {
                        fieldToUpdate = await JsonHelper.IsPatchRequiredAsync(currentData, json, excludedFields, null, logger, company) ?? string.Empty;
                        bool isPatchRequired = !string.IsNullOrWhiteSpace(fieldToUpdate);
                        if (!isPatchRequired) continue;

                        // Update: PATCH to items({id})
                        string updateUrl = $"{collectionUrl}({existingId})";

                        json = await JsonHelper.RemoveFieldsFromJsonAsync(json, excludedFields, logger, company);

                        await BcRequest.PatchBcDataAsync(client, updateUrl, getUrl, "no", json, etag ?? "*",
                            $"Item reference {itemNo} {referenceType} {referenceTypeNo} updated successfully.", $"Failed to update item reference {itemNo} {referenceType} {referenceTypeNo}. Json: {json}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);

                        var itemTempResult = await ItemBCRequest.GetItemsAsync(client, config, company, $"no eq '{itemNo.Replace("'", "''")}'", "", logger, authHelper, cancellationToken);
                        allItemData[itemNo] = itemTempResult[itemNo];

                        continue;
                    }

                    if (currentItemReferenceData == null || currentItemReferenceData.Count <= 0)
                    {
                        existingId = null;
                    }
                    else
                    {
                        foreach (var itemRef in currentItemReferenceData.Values)
                        {
                            itemRef.TryGetValue("systemId", out var systemId);
                            itemRef.TryGetValue("referenceTypeNo", out var tempTeferenceTypeNo);
                            itemRef.TryGetValue("referenceType", out var tempReferenceType);
                            itemRef.TryGetValue("referenceNo", out var tempReferenceNo);
                            itemRef.TryGetValue("unitOfMeasure", out var tempUnitOfMeasure);

                            if (referenceNo.Equals(tempReferenceNo, StringComparison.InvariantCultureIgnoreCase) &&
                                referenceType.Equals(tempReferenceType, StringComparison.InvariantCultureIgnoreCase) &&
                                unitOfMeasure.Equals(tempUnitOfMeasure, StringComparison.InvariantCultureIgnoreCase)
                                && (string.IsNullOrWhiteSpace(referenceTypeNo) && string.IsNullOrWhiteSpace(tempTeferenceTypeNo) ||
                                   (referenceTypeNo != null && referenceTypeNo.Equals(tempTeferenceTypeNo, StringComparison.InvariantCultureIgnoreCase))))
                            {
                                existingId = systemId;
                                break;
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(existingId))
                    {
                        string postUrl = collectionUrl;

                        await BcRequest.PostBcDataAsync(client, postUrl, json, $"Item reference {itemNo} {referenceType} {referenceTypeNo} created successfully.", $"Failed to create item reference {itemNo} {referenceType} {referenceTypeNo}. Json: {json}", EventLog.GetMethodName(), "", logger, company, authHelper, cancellationToken);

                        var itemTempResult = await ItemBCRequest.GetItemsAsync(client, config, company, $"no eq '{itemNo.Replace("'", "''")}'", "", logger, authHelper, cancellationToken);

                        allItemData ??= [];
                        if (!string.IsNullOrWhiteSpace(itemNo) && itemResult != null && itemTempResult.TryGetValue(itemNo, out _))
                        {
                            if (allItemData.TryGetValue(itemNo, out _))
                            {
                                allItemData[itemNo] = itemTempResult[itemNo];
                            }
                            else
                            {
                                allItemData.Add(itemNo, itemTempResult[itemNo]);
                            }
                        }
                    }
                }
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                ReasonPhrase = $"Item references {itemNo} checked."
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
    Dictionary<string, int>? uomDictionary = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (itemData is null || itemData.Count == 0)
                throw new ArgumentException("itemData required", nameof(itemData));

            if (uomDictionary is null || uomDictionary.Count == 0)
                throw new ArgumentException("uomDictionary required", nameof(uomDictionary));

            if (!itemData.TryGetValue("no", out var itemNumber) || string.IsNullOrEmpty(itemNumber))
                throw new ArgumentException("ItemNumber 'no' not found in itemData", nameof(itemData));

            var itemUnitOfMeasureCodes = JsonSerializer.Deserialize<Dictionary<string, object>[]>(itemData["itemUnitOfMeasures"] ?? "[]");

            var uomsToAddList = new Dictionary<string, int>();

            if (itemUnitOfMeasureCodes != null)
            {
                foreach (var uom in uomDictionary.Keys)
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
                        uomsToAddList.Add(uom, uomDictionary[uom]);
                }
            }
            else
            {
                foreach (string uom in uomDictionary.Keys)
                {
                    uomsToAddList.Add(uom, uomDictionary[uom]);
                }
            }

            foreach (string bcUom in uomsToAddList.Keys)
            {
                if (string.IsNullOrWhiteSpace(bcUom))
                    continue;

                // TRY ADD IT IN BC FIRST?
                try
                {
                    string postUomUrl = config[$"Companies:{company}:ItemUnitOfMeasureData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:ItemUnitOfMeasureData:DestinationApiUrl required in config");

                    var postData = new Dictionary<string, object>() { { "itemNo", itemNumber }, { "code", bcUom }, { "qtyPerUnitOfMeasure", uomsToAddList[bcUom] }, { "qtyRoundingPrecision", UomHelper.GetBcQtyRoundingPrecision(company, bcUom) } };

                    var postUomJson = JsonSerializer.Serialize(postData, new JsonSerializerOptions { WriteIndented = false });

                    string postUrl = $"{postUomUrl}";

                    await BcRequest.PostBcDataAsync(client, postUrl, postUomJson,
                         $"Item Uom {itemNumber} {bcUom} create successfully.", $"Failed to create item uom {itemNumber} {bcUom}. Json: {postUomJson}", EventLog.GetMethodName(), "", logger, company, authHelper, cancellationToken);
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
