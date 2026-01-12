using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static partial class BiebuyckItemsCsvToBCRequest
{
    public static async Task<Dictionary<string, Dictionary<string, string>>> GetItemsAsync(HttpClient client, IConfigurationRoot config, string? company = null, string filter = "", string expand = "", EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        return await ItemBCRequest.GetItemsAsync(client, config, company, filter, expand, logger, authHelper, cancellationToken);
    }

    public static async Task<HttpResponseMessage?> GetItemListAsync(HttpClient client, IConfigurationRoot config, string? company = null, List<string>? itemNumberList = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string json = ConvertCsvToItemJson(config, company, itemNumberList, logger).First();
        return await ItemBCRequest.UpsertItemAsync(client, config, company, json, null, logger, authHelper, cancellationToken);
    }

    public static async Task<HttpResponseMessage?> SyncItemsAsync(HttpClient client, IConfigurationRoot config, string company, Dictionary<string, Dictionary<string, string>>? allItemData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string json = ConvertCsvToItemJson(config, company, null, logger).First();
        return await ItemBCRequest.UpsertItemAsync(client, config, company, json, allItemData, logger, authHelper, cancellationToken);
    }

    public static async Task<string> ProcessItemsAsync(HttpClient client, IConfigurationRoot config, string? company = null, List<string>? itemNumberList = null, Dictionary<string, Dictionary<string, string>>? allItemData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        var stopwatchSuppliers = Stopwatch.StartNew();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Start processing items for company '{company}'.");
        foreach (string json in ConvertCsvToItemJson(config, company, itemNumberList, logger))
        {
            try
            {
                await ItemBCRequest.UpsertItemAsync(client, config, company, json, allItemData, logger, authHelper, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
            }
        }
        stopwatchSuppliers.Stop();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Finished processing items for company '{company}' in {StringHelper.GetDurationString(stopwatchSuppliers.Elapsed)}.");

        return "OK";
    }

    // Convert CSV rows to JSON request bodies suitable for Business Central items API.
    // Maps common columns from the provided sample CSV to a minimal BC item payload.
    public static IEnumerable<string> ConvertCsvToItemJson(IConfigurationRoot config, string? company = null, List<string>? itemNumberList = null, EventLog? logger = null)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string sourceType = config[$"Companies:{company}:ItemData:SourceType"] ?? "CSV";

        if (!sourceType.Equals("CSV", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Source type '{sourceType}' is not supported for customer data import.");
        }

        string csvPath = config[$"Companies:{company}:ItemData:Source"] ?? string.Empty;

        try
        {
            if (!File.Exists(csvPath))
                throw new FileNotFoundException("CSV file not found", csvPath);
        }
        catch (Exception)
        {
            throw new Exception("CSV file not found at: " + csvPath);
        }

        string csvDelimiter = config[$"Companies:{company}:ItemData:Delimiter"] ?? ",";
        string encodingName = config[$"Companies:{company}:ItemData:SourceEncoding"] ?? "UTF8";
        string itemNumberPrefix = config[$"Companies:{company}:ItemData:ItemNumberPrefix"] ?? "BI";
        bool useDefaultDimensions = bool.TryParse(config[$"Companies:{company}:ItemData:UseDefaultDimensions"], out bool dimensions) ? dimensions : false;

        var bcVatBusPostingGroupMapping = BiebuyckHelper.GetBcVatBusPostingGroupMapping(config, company);

        Encoding encoding = StringHelper.GetEncoding(encodingName);

        using var sr = new StreamReader(csvPath, encoding);
        string? headerLine = sr.ReadLine();
        if (headerLine == null)
            yield break;


        string? actualSkipped = null;

        if (encoding != Encoding.UTF8)
            headerLine = Encoding.UTF8.GetString(Encoding.Convert(encoding, Encoding.UTF8, encoding.GetBytes(headerLine)));

        string[] headers = CsvHelper.ParseCsvLine(headerLine, csvDelimiter);

        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            string json = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var map = CsvHelper.GetValueMap(encoding, line, headers, csvDelimiter);

                var item = new Dictionary<string, object>();

                // 'kode' appears to be the product code used in the CSV -> map to 'no'
                if (DictionaryHelper.TryGet(map, "kode", out string? no))
                {
                    no = itemNumberPrefix + no;
                    item["no"] = no;
                    // MUST BE SPECIFIC ITEM NUMBER
                    if (itemNumberList != null && itemNumberList.Count > 0 && !itemNumberList.Contains(no))
                        continue;
                }

                if (string.IsNullOrWhiteSpace(no))
                    continue;

                // Avoid writing logs for each line from same order ==> slows the proces down. So always safe last skipped docNum.
                if (!string.IsNullOrWhiteSpace(actualSkipped) && actualSkipped.Equals(no))
                    continue;

                actualSkipped = no;

                // displayName / description
                if (DictionaryHelper.TryGet(map, "omschr", out string? description) && !string.IsNullOrWhiteSpace(description))
                {
                    if (itemNumberList == null || itemNumberList.Count <= 0)
                    {
                        description = description.Trim();

                        if (description.StartsWith('/') || description.StartsWith('*') || description.StartsWith("zzz", StringComparison.InvariantCultureIgnoreCase))
                        {
                            if ((!description.StartsWith("/NB") &&
                            !description.StartsWith("/ NB") &&
                            !description.StartsWith("/ NU") &&
                            !description.StartsWith("/NU") &&
                            !description.StartsWith("/SB") &&
                            !description.StartsWith("/ SB") &&
                            !description.StartsWith("/SL") &&
                            !description.StartsWith("/ SL")) || description.StartsWith('*') || description.StartsWith("zzz", StringComparison.InvariantCultureIgnoreCase))
                            {
                                logger?.InfoAsync(EventLog.GetMethodName(), company, $"Inactive item code {no}").Wait();
                                continue;
                            }
                        }
                    }

                    // category or brand mapping to a generic 'searchDescription' to help identification
                    if (DictionaryHelper.TryGet(map, "merk", out string? merk) && !string.IsNullOrWhiteSpace(merk))
                    {
                        if (description.Contains(merk, StringComparison.InvariantCultureIgnoreCase))
                        {
                            description = description.Replace(merk, "", StringComparison.InvariantCultureIgnoreCase).Trim();
                        }

                        description = merk + " " + description;
                    }

                    item["description"] = StringHelper.CleanUpString(description);
                }
                else
                {
                    logger?.WarningAsync(EventLog.GetMethodName(), company, $"Skipping malformed CSV line, no item description {line}").Wait();
                    continue;
                }

                string? baseUnitOfMeasureCode = config[$"Companies:{company}:ItemData:BasicUnitOfMeasureDefault"] ?? "STUKS";
                item["baseUnitOfMeasure"] = baseUnitOfMeasureCode;
                item["salesUnitOfMeasure"] = config[$"Companies:{company}:ItemData:SalesUnitOfMeasureDefault"] ?? baseUnitOfMeasureCode;
                item["purchUnitOfMeasure"] =  config[$"Companies:{company}:ItemData:PurchaseUnitOfMeasureDefault"] ?? baseUnitOfMeasureCode;

                // unit of measure
                if (DictionaryHelper.TryGet(map, "eenheid", out string? tradeUnitOfMeasureCode) && !string.IsNullOrWhiteSpace(tradeUnitOfMeasureCode))
                    tradeUnitOfMeasureCode = UomHelper.GetBcUom(company, tradeUnitOfMeasureCode);                   

                if (string.IsNullOrWhiteSpace(tradeUnitOfMeasureCode))
                    tradeUnitOfMeasureCode = config[$"Companies:{company}:ItemData:TradeUnitOfMeasureDefault"] ?? "STUK";

                item["tradeUnitOfMeasure"] = tradeUnitOfMeasureCode;

                // Uoms
                var uomObjectList = new List<object>
                {
                    new Dictionary<string, object>
                        {
                            {"itemNo", no},
                            {"code", baseUnitOfMeasureCode},
                            {"qtyPerUnitOfMeasure", UomHelper.GetBcQtyPerUnitOfMeasure(company, baseUnitOfMeasureCode)},
                            {"qtyRoundingPrecision", UomHelper.GetBcQtyRoundingPrecision(company, baseUnitOfMeasureCode)},
                        }
                };
                
                if (!baseUnitOfMeasureCode.Equals(tradeUnitOfMeasureCode))
                    uomObjectList.Add(new Dictionary<string, object>
                        {
                            {"itemNo", no},
                            {"code", tradeUnitOfMeasureCode},
                            {"qtyPerUnitOfMeasure", UomHelper.GetBcQtyPerUnitOfMeasure(company, tradeUnitOfMeasureCode)},
                            {"qtyRoundingPrecision", UomHelper.GetBcQtyRoundingPrecision(company, tradeUnitOfMeasureCode)},
                        });
                
                if (uomObjectList.Count > 0)
                    item["itemUnitOfMeasures"] = uomObjectList;

                List<string> eanBarcodes = [];
                // vendor item number
                if (DictionaryHelper.TryGet(map, "artnr_lev", out string? vendorNo) && !string.IsNullOrWhiteSpace(vendorNo))
                {
                    item["vendorItemNo"] = vendorNo;

                    // it's possibly also a barcode
                    if (GtinValidator.IsValidGtin(vendorNo, out _)) eanBarcodes.Add(vendorNo);
                }

                // barcode / ean -> map to gtin if available
                if (DictionaryHelper.TryGet(map, "barcode", out string? barcode) && !string.IsNullOrWhiteSpace(barcode) && GtinValidator.IsValidGtin(barcode, out _) && !eanBarcodes.Contains(barcode))
                    eanBarcodes.Add(barcode);

                if (DictionaryHelper.TryGet(map, "ean", out string? ean) && !string.IsNullOrWhiteSpace(ean) && GtinValidator.IsValidGtin(ean, out _) && !eanBarcodes.Contains(ean))
                    eanBarcodes.Add(ean);

                if (DictionaryHelper.TryGet(map, "bar1", out string? bar1) && !string.IsNullOrWhiteSpace(bar1) && GtinValidator.IsValidGtin(bar1, out _) && !eanBarcodes.Contains(bar1))
                    eanBarcodes.Add(bar1);

                if (DictionaryHelper.TryGet(map, "bar2", out string? bar2) && !string.IsNullOrWhiteSpace(bar2) && GtinValidator.IsValidGtin(bar2, out _) && !eanBarcodes.Contains(bar2))
                    eanBarcodes.Add(bar2);

                if (DictionaryHelper.TryGet(map, "bar3", out string? bar3) && !string.IsNullOrWhiteSpace(bar3) && GtinValidator.IsValidGtin(bar3, out _) && !eanBarcodes.Contains(bar3))
                    eanBarcodes.Add(bar3);

                if (DictionaryHelper.TryGet(map, "bar4", out string? bar4) && !string.IsNullOrWhiteSpace(bar4) && GtinValidator.IsValidGtin(bar4, out _) && !eanBarcodes.Contains(bar4))
                    eanBarcodes.Add(bar4);

                if (eanBarcodes.Count > 0)
                {
                    item["gtin"] = eanBarcodes[0];

                    var eanBarcodeList = new List<object>();
                    foreach (var eanBarcode in eanBarcodes)
                    {
                        eanBarcodeList.Add(new Dictionary<string, object>
                            {
                                {"itemNo", no},
                                {"referenceType", ItemReferencesBCRequest.ReferenceType_BarCode},
                                {"referenceNo", eanBarcode},
                                {"unitOfMeasure", tradeUnitOfMeasureCode}
                            });
                    }

                    item["itemReferences"] = eanBarcodeList;
                }

                if (DictionaryHelper.TryGet(map, "groep", out string? orderItem) && !string.IsNullOrWhiteSpace(orderItem))
                {
                    if (orderItem.Equals("BEST", StringComparison.OrdinalIgnoreCase))
                        item["orderItem"] = true;
                    else
                        item["orderItem"] = false;
                }

                // VAT
                if (DictionaryHelper.TryGet(map, "sttel", out string? vatPercentage) && StringHelper.TryParseDouble(vatPercentage, CultureInfo.CurrentCulture, out double taxRate) && bcVatBusPostingGroupMapping.TryGetValue(taxRate, out string? bcTaxCode) && !string.IsNullOrWhiteSpace(bcTaxCode))
                {
                    item["vatProdPostingGroup"] = bcTaxCode;
                }
                else
                {
                    item["vatProdPostingGroup"] = config[$"Companies:{company}:ItemData:VatBusPostingGroupDefault"] ?? "G1";
                }

                // DEFAULT VALUES BC
                item["itemDiscGroup"] = config[$"Companies:{company}:ItemData:ItemDiscountGroupDefault"] ?? "ALLE";
                item["showInCompany"] = config[$"Companies:{company}:ItemData:ShowInCompanyDefault"] ?? "";
                item["inventoryPostingGroup"] = config[$"Companies:{company}:ItemData:InventoryPostingGroupDefault"] ?? "NORMAAL";
                item["genProdPostingGroup"] = config[$"Companies:{company}:ItemData:GenBusPostingGroupDefault"] ?? "HDG";

                if (useDefaultDimensions)
                {
                    var dimension = DimensionBCRequest.GetItemCogsDimension(config, "BIEBUYCK", no);

                    item["defaultDimensions"] = new List<object>() { dimension };
                }

                // Serialize to compact JSON suitable as a request body
                json = JsonSerializer.Serialize(item, new JsonSerializerOptions { WriteIndented = false });
            }
            catch (Exception)
            {
                logger?.WarningAsync(EventLog.GetMethodName(), company, $"Skipping malformed CSV line: {line}").Wait();
                continue;
            }

            yield return json;
        }
    }

    
}
