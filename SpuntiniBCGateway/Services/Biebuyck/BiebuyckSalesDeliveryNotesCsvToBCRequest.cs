using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static class BiebuyckSalesDeliveryNotesCsvToBCRequest
{
    public static async Task<Dictionary<string, Dictionary<string, string>>> GetSalesOrdersAsync(HttpClient client, IConfigurationRoot config, string? company = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string customerUrl = config[$"Companies:{company}:SalesOrderData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:SalesOrderData:DestinationApiUrl required in config");
        customerUrl += config[$"Companies:{company}:SalesOrderData:SelectAllFilter"] ?? "";

        return await BcRequest.GetBcDataAsync(client, customerUrl, "number", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
    }

    public static async Task<HttpResponseMessage?> GetSalesOrderListAsync(HttpClient client, IConfigurationRoot config, string? company = null, List<string>? documentNumberList = null, Dictionary<string, Dictionary<string, string>>? allItemData = null, Dictionary<string, Dictionary<string, string>>? allCustomerData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        await foreach (string? json in ConvertCsvToSalesOrderJsonAsync(config, client, company, documentNumberList, allItemData, allCustomerData, logger, authHelper, cancellationToken))
            return await SalesOrderBCRequest.UpsertSalesOrderAsync(client, config, company, json, logger, authHelper, cancellationToken);

        return null;
    }

    public static async Task<HttpResponseMessage?> SyncSalesOrdersAsync(HttpClient client, IConfigurationRoot config, string? company = null, Dictionary<string, Dictionary<string, string>>? allItemData = null, Dictionary<string, Dictionary<string, string>>? allCustomerData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        await foreach (string? json in ConvertCsvToSalesOrderJsonAsync(config, client, company, null, allItemData, allCustomerData, logger, authHelper, cancellationToken))
            return await SalesOrderBCRequest.UpsertSalesOrderAsync(client, config, company, json, logger, authHelper, cancellationToken);
        return null;
    }

    public static async Task<string> ProcessSalesOrdersAsync(HttpClient client, IConfigurationRoot config, string? company = null, List<string>? documentNumberList = null, Dictionary<string, Dictionary<string, string>>? allItemData = null, Dictionary<string, Dictionary<string, string>>? allCustomerData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        var stopwatchSuppliers = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Start processing sales orders for company '{company}'.");

        await foreach (string? json in ConvertCsvToSalesOrderJsonAsync(config, client, company, documentNumberList, allItemData, allCustomerData, logger, authHelper, cancellationToken))
        {
            try
            {
                var resp = await SalesOrderBCRequest.UpsertSalesOrderAsync(client, config, company, json, logger, authHelper, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
            }
        }
        stopwatchSuppliers.Stop();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Finished processing sales orders for company '{company}' in {StringHelper.GetDurationString(stopwatchSuppliers.Elapsed)}.");

        return "OK";
    }

    // Parse CSV and group rows by 'nummer1' (column A).
    // For each group, extract header (columns A-F + P) and line details (columns G-O).
    public static async IAsyncEnumerable<string?> ConvertCsvToSalesOrderJsonAsync(IConfigurationRoot config, HttpClient httpClient, string? company = null, List<string>? documentNumberList = null, Dictionary<string, Dictionary<string, string>>? allItemData = null, Dictionary<string, Dictionary<string, string>>? allCustomerData = null, EventLog? logger = null, AuthHelper? authHelper = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allItemData);
        ArgumentNullException.ThrowIfNull(allCustomerData);

        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string sourceType = config[$"Companies:{company}:SalesOrderData:SourceType"] ?? "CSV";

        if (!sourceType.Equals("CSV", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Source type '{sourceType}' is not supported for customer data import.");
        }

        string csvPath = config[$"Companies:{company}:SalesOrderData:Source"] ?? string.Empty;

        try
        {
            if (!File.Exists(csvPath))
                throw new FileNotFoundException("CSV file not found", csvPath);
        }
        catch (Exception)
        {
            throw new Exception("CSV file not found at: " + csvPath);
        }

        string csvDelimiter = config[$"Companies:{company}:SalesOrderData:Delimiter"] ?? ",";
        string encodingName = config[$"Companies:{company}:SalesOrderData:SourceEncoding"] ?? "UTF8";
        string firstDayToProces = config[$"Companies:{company}:SalesOrderData:FirstDayToProces"] ?? "UTF8";
        string itemNumberPrefix = config[$"Companies:{company}:ItemData:ItemNumberPrefix"] ?? "BI";
        string customerNumberPrefix = config[$"Companies:{company}:CustomerData:CustomerNumberPrefix"] ?? "SPBI25";
        int processHorizonLastXDays = int.TryParse(config[$"Companies:{company}:SalesOrderData:ProcessHorizonLastXDays"], out int horizon) ? horizon : 14;
        Encoding encoding = StringHelper.GetEncoding(encodingName);
        List<string> unknowItemList = [];
        List<string> unknowCustomerList = [];
        using var sr = new StreamReader(csvPath, encoding);
        string? headerLine = sr.ReadLine();
        if (headerLine == null)
            yield break;

        if (encoding != Encoding.UTF8)
            headerLine = Encoding.UTF8.GetString(Encoding.Convert(encoding, Encoding.UTF8, encoding.GetBytes(headerLine)));

        string[] headers = CsvHelper.ParseCsvLine(headerLine, csvDelimiter);

        // Read all rows and group by 'nummer1'
        var groupedByDocument = new Dictionary<string, List<Dictionary<string, string>>>();

        string? actualSkippedDocNum = null;

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
                    // Avoid writing logs for each line from same order ==> slows the proces down. So always safe last skipped docNum.
                    if (!string.IsNullOrWhiteSpace(actualSkippedDocNum) && actualSkippedDocNum.Equals(docNum))
                        continue;                        

                    if (documentNumberList == null || documentNumberList.Count <= 0)
                    {      
                        if (DictionaryHelper.TryGet(map, "datum", out string? docDate) && !string.IsNullOrWhiteSpace(docDate))
                        {
                            var docDateDateTime = BiebuyckHelper.ParseToDateTime(docDate);
                            if (DateTime.TryParse(firstDayToProces, out var firstDay) && firstDay > docDateDateTime)
                            {
                                actualSkippedDocNum = docNum;
                                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Skipping CSV line, {docNum} with docdate {docDate} is before first day to process {firstDayToProces}");
                                continue;
                            }

                            if (docDateDateTime is not null && docDateDateTime <= DateTime.Now.AddDays(-processHorizonLastXDays))
                            {
                                actualSkippedDocNum = docNum;
                                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Skipping CSV line, {docNum} with docdate {docDate} is not within the horizon scope of last {processHorizonLastXDays} days");
                                continue;
                            }

                            if (docDateDateTime is not null && docDateDateTime <= DateTime.Now.AddDays(-processHorizonLastXDays))
                            {
                                actualSkippedDocNum = docNum;
                                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Skipping CSV line, {docNum} with docdate {docDate} is not within the horizon scope of last {processHorizonLastXDays} days");
                                continue;
                            }
                        }
                        else
                        {
                            actualSkippedDocNum = docNum;
                            if (logger != null) await logger.WarningAsync(EventLog.GetMethodName(), company, $"Skipping malformed CSV line, no docdate");
                            continue;
                        }
                    }
                    else if (!documentNumberList.Contains(docNum))
                    {
                        actualSkippedDocNum = docNum;
                        continue;
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

        // For each document group, produce a sales order JSON
        foreach (string docNum in groupedByDocument.Keys)
        {
            var rows = groupedByDocument[docNum];
            if (rows.Count == 0)
                continue;

            var firstRow = rows[0];

            var document = new Dictionary<string, object>();

            // Header fields from first row (columns A-F + P)
            if (DictionaryHelper.TryGet(firstRow, "nummer1", out string? docNumber) && docNumber != null)
                document["number"] = docNumber;

            string? customerNumber = null;
            if (DictionaryHelper.TryGet(firstRow, "klantnummer", out string? number) && number != null)
                customerNumber = customerNumberPrefix + number;

            if (string.IsNullOrWhiteSpace(customerNumber))
            {
                if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, new Exception($"Customer not found for document {docNumber}"));
                continue;
            }

            if (!allCustomerData.TryGetValue(customerNumber, out var customerData))
            {
                try
                {
                    if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Customer {customerNumber} not found in cache, retrieving from Biebuyck file.");

                    await BiebuyckCustomerCsvToBCRequest.GetCustomerListAsync(httpClient, config, company, [customerNumber], logger, authHelper, cancellationToken);

                    var customerTempData = await BiebuyckCustomerCsvToBCRequest.GetCustomersAsync(httpClient, config, company, $"no eq '{customerNumber}'", logger, authHelper, cancellationToken);

                    if (customerTempData.TryGetValue(customerNumber, out customerData))
                    {
                        allCustomerData[customerNumber] = customerData;
                    }
                    else
                    {
                        if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, new Exception($"Customer {customerNumber} unknown for document {docNumber}"));
                        continue;
                    }
                }
                catch (Exception)
                {
                    if (unknowCustomerList.Contains(customerNumber))
                    {
                        if (logger != null) await logger.WarningAsync(EventLog.GetMethodName(), company, $"Customer {customerNumber} not found in file, but already flagged");
                        continue;
                    }

                    unknowCustomerList.Add(customerNumber);

                    if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, new Exception($"Customer {customerNumber} unknown for document {docNumber}"));
                    continue;
                }

                if (customerData == null)
                {
                    if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, new Exception($"Customer {customerNumber} unknown for document {docNumber}"));
                    continue;
                }
            }

            if (customerData.TryGetValue("blocked", out var isBlocked) && !string.IsNullOrWhiteSpace(isBlocked) && isBlocked != "_x0020_")
            {
                if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, new Exception($"Customer {customerNumber} is blocked, unblock customer."));

                string customerUrl = config[$"Companies:{company}:CustomerData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:CustomerData:DestinationApiUrl required in config");

                customerData.TryGetValue("systemId", out var existingId);
                customerData.TryGetValue("@odata.etag", out var etag);

                var patchJson = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        { "blocked", "" }
                    });

                await BcRequest.PatchBcDataAsync(httpClient, patchUrl: $"{customerUrl}({existingId})", getUrl: $"{customerUrl}?$filter=no eq '{customerNumber.Replace("'", "''")}'", keyValue: "no", json: patchJson, etag: etag ?? "*",
                succesMessage: $"Customer {customerNumber} deblocked successfully.", errorMessage: $"Failed to deblock customer {customerNumber}. Json: {patchJson}", sourceMethod: EventLog.GetMethodName(), logger: logger, company: company, authHelper: authHelper, cancellationToken: cancellationToken);

                customerData["blocked"] = "";
            }

            document["customerNumber"] = customerNumber;
            document["billToCustomerNumber"] = customerNumber;

            if (DictionaryHelper.TryGet(firstRow, "datum", out string? deliveryDate) && deliveryDate != null)
            {
                string? parsedDate = BiebuyckHelper.ParseDate(deliveryDate);
                if (!string.IsNullOrWhiteSpace(parsedDate))
                {
                    document["orderDate"] = parsedDate;
                    document["postingDate"] = parsedDate;
                }
            }

            DictionaryHelper.TryGet(firstRow, "betaald_kas", out string? payment);

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
                                await BiebuyckItemsCsvToBCRequest.GetItemListAsync(httpClient, config, company, [lineObjectNumber], logger, authHelper, cancellationToken);
                            }

                            string escaped = lineObjectNumber.Replace("'", "''");
                            var filter = $"no eq '{escaped}'";

                            string collectionUrl = config[$"Companies:{company}:ItemData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:ItemData:DestinationApiUrl required in config");

                            string getUrl = collectionUrl + "?$filter=" + filter + "&$expand=itemUnitOfMeasures";
                            itemData = (await BcRequest.GetBcDataAsync(httpClient, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

                            allItemData[lineObjectNumber] = itemData;
                        }
                        catch (Exception)
                        {
                            if (unknowItemList.Contains(lineObjectNumber))
                            {
                                logger?.WarningAsync(EventLog.GetMethodName(), company, $"Item {lineObjectNumber} not found in file, but already flagged").Wait();
                                continue;
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
                        documentLine["lineObjectNumber"] = lineObjectNumber;
                        documentLine["lineType"] = "Item";

                        if (DictionaryHelper.TryGet(row, "stukprijs", out string? unitPrice) && unitPrice != null && StringHelper.TryParseDouble(unitPrice, CultureInfo.CurrentCulture, out double price))
                        {
                            priceNegative = price < 0;

                            // Price must be positive for Peppol
                            documentLine["unitPrice"] = priceNegative ? -price : price;
                        }

                        if (priceNegative && qty >= 0) qty = -qty;
                        documentLine["quantity"] = qty;
                        documentLine["shipQuantity"] = qty;

                        if (DictionaryHelper.TryGet(row, "lijnperc", out string? lineDiscountPercent) && lineDiscountPercent != null && StringHelper.TryParseDouble(lineDiscountPercent, CultureInfo.CurrentCulture, out double discountPercent))
                        {
                            documentLine["discountPercent"] = discountPercent;
                        }

                        DictionaryHelper.TryGet(row, "eenheid", out string? uom);

                        string? bcUom = UomHelper.GetBcUom(company, uom, itemData["tradeUnitOfMeasure"]);

                        if (!string.IsNullOrWhiteSpace(bcUom) && !bcUom.Equals(itemData["tradeUnitOfMeasure"], StringComparison.InvariantCultureIgnoreCase))
                        {
                            await ItemUnitOfMeasureBCRequest.AddItemUnitOfMeasureAsync(httpClient, config, company, itemData, new Dictionary<string, int> {{ bcUom, UomHelper.GetBcQtyPerUnitOfMeasure(company, bcUom) }}, logger, authHelper, cancellationToken).ConfigureAwait(false);

                            documentLine["unitOfMeasureCode"] = bcUom;
                        }

                        documentLine["unitOfMeasureCode"] = string.IsNullOrWhiteSpace(bcUom) ? (config[$"Companies:{company}:ItemData:BasicUnitOfMeasureDefault"] ?? "STUKS") : bcUom;

                        if (DictionaryHelper.TryGet(row, "lotnr", out string? lotnr) && !string.IsNullOrWhiteSpace(lotnr))
                            documentLine["description2"] = lotnr;
                    }
                    else
                    {
                        // ZERO QTY LINES ARE NOT ALLOWED BUT ARE GOODS THAT WHERE NOT DELIVERED TO THE CUSTOMER ==> IN BACKORDER
                        // WE WILL ADD THEM AS INFORMATION TEXT ON THE ORDER
                        documentLine["lineType"] = "Comment";
                        description = string.IsNullOrWhiteSpace(description) ? lineObjectNumber : $"{lineObjectNumber}: {description} (Backorder)";
                    }
                }
                else
                {
                    documentLine["lineType"] = "Comment";
                }

                if (!string.IsNullOrWhiteSpace(description))
                    documentLine["description"] = description;

                documentLines.Add(documentLine);
            }

            if (errorInLines) continue;

            if (!string.IsNullOrWhiteSpace(payment))
            {
                var documentLine = new Dictionary<string, object>
                {
                    ["lineType"] = "Comment",
                    ["description"] = "Geregistreerde betaling in kas incl.BTW: €" + payment
                };

                documentLines.Add(documentLine);
            }

            if (documentLines.Count > 0)
            {
                document["salesOrderLines"] = documentLines;
                string json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = false });
                yield return json;
            }
        }
    }
}
