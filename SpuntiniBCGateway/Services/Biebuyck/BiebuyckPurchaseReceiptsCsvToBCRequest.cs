using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static class BiebuyckPurchaseReceiptsCsvToBCRequest
{
    public static async Task<Dictionary<string, Dictionary<string, string>>> GetPurchaseOrdersAsync(HttpClient client, IConfigurationRoot config, string? company = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string supplierUrl = config[$"Companies:{company}:PurchaseOrderData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:PurchaseOrderData:DestinationApiUrl required in config");
        supplierUrl += config[$"Companies:{company}:PurchaseOrderData:SelectAllFilter"] ?? "";

        return await BcRequest.GetBcDataAsync(client, supplierUrl, "number", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
    }

    public static async Task<HttpResponseMessage?> GetPurchaseOrderAsync(HttpClient client, IConfigurationRoot config, string? company = null, string? documentNumber = null, Dictionary<string, Dictionary<string, string>>? allItemData = null, Dictionary<string, Dictionary<string, string>>? allSupplierData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        await foreach (string? json in ConvertCsvToPurchaseOrderJsonAsync(config, company, client, documentNumber, allItemData, allSupplierData, logger, authHelper, cancellationToken))
            return await PurchaseOrderBCRequest.UpsertPurchaseOrderAsync(client, config, company, json, logger, authHelper, cancellationToken);

        return null;
    }

    public static async Task<HttpResponseMessage?> SyncPurchaseOrdersAsync(HttpClient client, IConfigurationRoot config, string? company = null, Dictionary<string, Dictionary<string, string>>? allItemData = null, Dictionary<string, Dictionary<string, string>>? allSupplierData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        await foreach (string? json in ConvertCsvToPurchaseOrderJsonAsync(config, company, client, null, allItemData, allSupplierData, logger, authHelper, cancellationToken))
            return await PurchaseOrderBCRequest.UpsertPurchaseOrderAsync(client, config, company, json, logger, authHelper, cancellationToken);
        return null;
    }

    public static async Task<string> ProcessPurchaseOrdersAsync(HttpClient client, IConfigurationRoot config, string? company = null, Dictionary<string, Dictionary<string, string>>? allItemData = null, Dictionary<string, Dictionary<string, string>>? allSupplierData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        var stopwatchSuppliers = Stopwatch.StartNew();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Start processing purchase orders for company '{company}'.");

        if (allSupplierData == null)
        {
            if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"No suppliers known for company '{company}'.");
            return "No suppliers known.";
        }

        await foreach (string? json in ConvertCsvToPurchaseOrderJsonAsync(config, company, client, null, allItemData, allSupplierData, logger, authHelper, cancellationToken))
        {
            try
            {
                var resp = await PurchaseOrderBCRequest.UpsertPurchaseOrderAsync(client, config, company, json, logger, authHelper, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
            }
        }
        stopwatchSuppliers.Stop();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Finished processing purchase orders for company '{company}' in {StringHelper.GetDurationString(stopwatchSuppliers.Elapsed)}.");

        return "OK";
    }

    // Parse CSV and group rows by 'nummer1' (column A).
    // For each group, extract header (columns A-F + P) and line details (columns G-O).
    public static async IAsyncEnumerable<string?> ConvertCsvToPurchaseOrderJsonAsync(IConfigurationRoot config, string? company = null, HttpClient? httpClient = null, string? documentNumber = null, Dictionary<string, Dictionary<string, string>>? allItemData = null, Dictionary<string, Dictionary<string, string>>? allSupplierData = null, EventLog? logger = null, AuthHelper? authHelper = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allItemData);
        ArgumentNullException.ThrowIfNull(allSupplierData);
        ArgumentNullException.ThrowIfNull(httpClient);

        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string sourceType = config[$"Companies:{company}:PurchaseOrderData:SourceType"] ?? "CSV";

        if (!sourceType.Equals("CSV", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Source type '{sourceType}' is not supported for supplier data import.");
        }

        string csvPath = config[$"Companies:{company}:PurchaseOrderData:Source"] ?? string.Empty;

        try
        {
            if (!File.Exists(csvPath))
                throw new FileNotFoundException("CSV file not found", csvPath);
        }
        catch (Exception)
        {
            throw new Exception("CSV file not found at: " + csvPath);
        }

        string csvDelimiter = config[$"Companies:{company}:PurchaseOrderData:Delimiter"] ?? ",";
        string encodingName = config[$"Companies:{company}:PurchaseOrderData:SourceEncoding"] ?? "UTF8";
        string firstDayToProces = config[$"Companies:{company}:PurchaseOrderData:FirstDayToProces"] ?? "UTF8";
        string itemNumberPrefix = config[$"Companies:{company}:ItemData:ItemNumberPrefix"] ?? "BI";
        string locationCode = config[$"Companies:{company}:PurchaseOrderData:LocationCodeDefault"] ?? "RUISELEDE";
        int processHorizonLastXDays = int.TryParse(config[$"Companies:{company}:PurchaseOrderData:ProcessHorizonLastXDays"], out int horizon) ? horizon : 14;

        Encoding encoding = StringHelper.GetEncoding(encodingName);
        List<string> unknowItemList = [];

        using var sr = new StreamReader(csvPath, encoding);
        string? headerLine = sr.ReadLine();
        if (headerLine == null)
            yield break;

        if (encoding != Encoding.UTF8)
            headerLine = Encoding.UTF8.GetString(Encoding.Convert(encoding, Encoding.UTF8, encoding.GetBytes(headerLine)));

        string[] headers = CsvHelper.ParseCsvLine(headerLine, csvDelimiter);

        // Read all rows and group by 'nummer1'
        var groupedByDocument = new Dictionary<string, List<Dictionary<string, string>>>();

        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var map = CsvHelper.GetValueMap(encoding, line, headers, csvDelimiter);

                // CHECK FIRST IF THE DOCUMENT IS WITHIN THE HORIZON OF THE LATEST X DAYS
                if (DictionaryHelper.TryGet(map, "nummer1", out string? docNum) && !string.IsNullOrWhiteSpace(docNum))
                {
                    if (string.IsNullOrWhiteSpace(documentNumber) || !docNum.Equals(documentNumber))
                    {
                        if (DictionaryHelper.TryGet(map, "datum", out string? docDate) && !string.IsNullOrWhiteSpace(docDate))
                        {
                            var docDateDateTime = BiebuyckHelper.ParseToDateTime(docDate);
                            if (DateTime.TryParse(firstDayToProces, out var firstDay) && firstDay > docDateDateTime)
                            {
                                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Skipping CSV line, {docNum} with docdate {docDate} is before first day to process {firstDayToProces}");
                                continue;
                            }

                            if (docDateDateTime is not null && docDateDateTime <= DateTime.Now.AddDays(-processHorizonLastXDays))
                            {
                                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Skipping CSV line, {docNum} with docdate {docDate} is not within the horizon scope of last {processHorizonLastXDays} days");
                                continue;
                            }

                            if (docDateDateTime is not null && docDateDateTime <= DateTime.Now.AddDays(-processHorizonLastXDays))
                            {
                                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Skipping CSV line, {docNum} with docdate {docDate} is not within the horizon scope of last {processHorizonLastXDays} days");
                                continue;
                            }
                        }
                        else
                        {
                            if (logger != null) await logger.WarningAsync(EventLog.GetMethodName(), company, $"Skipping malformed CSV line, no docdate");
                            continue;
                        }
                    }

                    if (!groupedByDocument.ContainsKey(docNum))
                        groupedByDocument[docNum] = [];
                    groupedByDocument[docNum].Add(map);
                }
                else
                {
                    if (logger != null) await logger.WarningAsync(EventLog.GetMethodName(), company, $"Skipping malformed CSV line, no docnum");
                    continue;
                }
            }
            catch
            {
                if (logger != null) await logger.WarningAsync(EventLog.GetMethodName(), company, $"Skipping malformed CSV line");
                continue;
            }
        }

        // For each document group, produce a Purchase order JSON
        foreach (string docNum in groupedByDocument.Keys)
        {
            var rows = groupedByDocument[docNum];
            if (rows.Count == 0)
                continue;

            var firstRow = rows[0];

            var document = new Dictionary<string, object>();

            // Header fields from first row (columns A-F + P)
            if (DictionaryHelper.TryGet(firstRow, "nummer1", out string? docNumber) && docNumber != null)
                document["no"] = docNumber;

            if (!DictionaryHelper.TryGet(firstRow, "levnummer", out string? supplierNumber) || string.IsNullOrWhiteSpace(supplierNumber))
            {
                if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, new Exception($"Supplier not found for document {docNumber}"));
                continue;
            }

            if (!allSupplierData.TryGetValue(supplierNumber, out var supplierData))
            {
                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Supplier {supplierNumber} not found in cache, must be linked in BC.");

                continue;
            }

            var vendorNo = supplierData["no"];

            if (supplierData.TryGetValue("blocked", out var isBlocked) && !string.IsNullOrWhiteSpace(isBlocked) && isBlocked != "_x0020_")
            {
                if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, new Exception($"Supplier {vendorNo} is blocked, unblock supplier."));

                string supplierUrl = config[$"Companies:{company}:SupplierData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:SupplierData:DestinationApiUrl required in config");

                supplierData.TryGetValue("systemId", out var existingId);
                supplierData.TryGetValue("@odata.etag", out var etag);

                var patchJson = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        { "blocked", "" }
                    });

                await BcRequest.PatchBcDataAsync(httpClient, $"{supplierUrl}({existingId})", patchJson, etag ?? "*",
                $"Supplier {vendorNo} deblocked successfully.", $"Failed to deblock supplier {vendorNo}. Json: {patchJson}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);

                supplierData["blocked"] = "";
            }

            document["buyFromVendorNo"] = vendorNo;
            document["payToVendorNo"] = vendorNo;

            if (DictionaryHelper.TryGet(firstRow, "datum", out string? deliveryDate) && deliveryDate != null)
            {
                string? parsedDate = BiebuyckHelper.ParseDate(deliveryDate);
                if (!string.IsNullOrWhiteSpace(parsedDate))
                {
                    document["orderDate"] = parsedDate;
                    document["documentDate"] = parsedDate;
                    document["postingDate"] = parsedDate;
                }
            }

            if (DictionaryHelper.TryGet(firstRow, "bonnrlev", out var yourReference) && yourReference != null)
                document["yourReference"] = yourReference;

            // Line details (columns G-O for each row; skip header-only rows)
            var documentLines = new List<Dictionary<string, object>>();

            bool errorInLines = false;
            foreach (var row in rows)
            {
                DictionaryHelper.TryGet(row, "prdkode", out string? lineObjectNumber);
                DictionaryHelper.TryGet(row, "prdomschr", out string? description);

                if (string.IsNullOrWhiteSpace(lineObjectNumber) && string.IsNullOrWhiteSpace(description))
                    continue;

                var documentLine = new Dictionary<string, object>();

                if (!string.IsNullOrWhiteSpace(lineObjectNumber))
                {
                    // POSSIBLE USAGE OF SPUNTINI ITEMS ==> PREFIX WITH 'A' TO AVOID CONFLICT WITH BIEBUYCK ITEMS
                    if (!lineObjectNumber.StartsWith("A", StringComparison.InvariantCultureIgnoreCase))
                        lineObjectNumber = itemNumberPrefix + lineObjectNumber;

                    if (!allItemData.TryGetValue(lineObjectNumber, out var itemData))
                    {
                        try
                        {
                            if (!lineObjectNumber.StartsWith("A", StringComparison.InvariantCultureIgnoreCase))
                            {
                                await BiebuyckItemsCsvToBCRequest.GetItemAsync(httpClient, config, company, lineObjectNumber, logger, authHelper, cancellationToken);
                            }

                            string escaped = lineObjectNumber.Replace("'", "''");
                            var filter = $"no eq '{escaped}'";

                            string collectionUrl = config[$"Companies:{company}:ItemData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:ItemData:DestinationApiUrl required in config");

                            string getUrl = collectionUrl + "?$filter=" + Uri.EscapeDataString(filter) + "&$expand=itemUnitOfMeasures";
                            itemData = (await BcRequest.GetBcDataAsync(httpClient, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

                            allItemData[lineObjectNumber] = itemData;
                        }
                        catch (Exception)
                        {
                            if (unknowItemList.Contains(lineObjectNumber))
                            {
                                logger?.WarningAsync(EventLog.GetMethodName(), company, $"Item {lineObjectNumber} not found in file, but already flagged").Wait();
                                break;
                            }

                            unknowItemList.Add(lineObjectNumber);

                            if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, new Exception($"Item {lineObjectNumber} unknown for document {docNumber}"));
                            errorInLines = true;
                            break;
                        }
                    }

                    bool priceNegative = false;
                    double qty = 0d;

                    if (DictionaryHelper.TryGet(row, "aantal", out string? quantity) && quantity != null && StringHelper.TryParseDouble(quantity, CultureInfo.CurrentCulture, out qty))
                    {
                        if (priceNegative && qty >= 0) qty = -qty;
                    }

                    if (qty != 0d && itemData != null)
                    {
                        double price = 0d;
                        if (DictionaryHelper.TryGet(row, "stukprijs", out string? unitPrice) && unitPrice != null && StringHelper.TryParseDouble(unitPrice, CultureInfo.CurrentCulture, out price))
                        {
                            priceNegative = price < 0;
                        }

                        if (priceNegative && qty >= 0) qty = -qty;

                        DictionaryHelper.TryGet(row, "eenheid", out string? uom);

                        string? bcUom = UomHelper.GetBcUom(company, uom, itemData["tradeUnitOfMeasure"]);

                        if (!string.IsNullOrWhiteSpace(bcUom) && !bcUom.Equals(itemData["tradeUnitOfMeasure"], StringComparison.InvariantCultureIgnoreCase))
                        {
                            await ItemUnitOfMeasureBCRequest.AddItemUnitOfMeasureAsync(httpClient, config, company, itemData, [bcUom], logger, authHelper, cancellationToken).ConfigureAwait(false);
                        }

                        documentLine["type"] = "Item";
                        documentLine["no"] = lineObjectNumber;
                        documentLine["locationCode"] = locationCode;
                        documentLine["unitOfMeasureCode"] = string.IsNullOrWhiteSpace(bcUom) ? (config[$"Companies:{company}:ItemData:BasicUnitOfMeasureDefault"] ?? "STUKS") : bcUom;

                        // Price must be positive for Peppol
                        documentLine["directUnitCost"] = priceNegative ? -price : price;
                        documentLine["quantity"] = qty;

                        if (DictionaryHelper.TryGet(row, "lotnr", out string? lotnr) && !string.IsNullOrWhiteSpace(lotnr))
                            documentLine["description2"] = lotnr;
                    }
                    else
                    {
                        // ZERO QTY LINES ARE NOT ALLOWED BUT ARE GOODS THAT WHERE NOT DELIVERED TO THE CUSTOMER ==> IN BACKORDER
                        // WE WILL ADD THEM AS INFORMATION TEXT ON THE ORDER
                        description = string.IsNullOrWhiteSpace(description) ? lineObjectNumber : $"{lineObjectNumber}: {description} (Backorder)";
                    }
                }

                if (!string.IsNullOrWhiteSpace(description))
                    documentLine["description"] = description;

                documentLines.Add(documentLine);
            }

            if (errorInLines) continue;

            if (documentLines.Count > 0)
            {
                document["documentType"] = "Order";
                document["locationCode"] = locationCode;
                document["shipmentMethodCode"] = config[$"Companies:{company}:PurchaseOrderData:ShipmentMethodCode"] ?? "LEVERING";
                document["purchaseLines"] = documentLines;
                string json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = false });
                yield return json;
            }
        }
    }
}
