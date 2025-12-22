using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static partial class BiebuyckItemsCsvToBCRequest
{
    public static async Task<Dictionary<string, Dictionary<string, string>>> GetItemsAsync(HttpClient client, IConfigurationRoot config, string? company = null, string filter = "", EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string itemUrl = config[$"Companies:{company}:ItemData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:ItemData:DestinationApiUrl required in config");
        bool useDefaultDimensions = bool.TryParse(config[$"Companies:{company}:ItemData:UseDefaultDimensions"], out bool dimensions) ? dimensions : false;
        
        if (string.IsNullOrWhiteSpace(filter))
            filter = config[$"Companies:{company}:ItemData:SelectAllFilter"] ?? "";

        if (string.IsNullOrWhiteSpace(filter))
            return await BcRequest.GetBcDataAsync(client, itemUrl + (useDefaultDimensions ? "?$expand=defaultDimensions,itemUnitOfMeasures" : "?$expand=itemUnitOfMeasures"), "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);

        return await BcRequest.GetBcDataAsync(client, itemUrl + filter + (useDefaultDimensions ? "&$expand=defaultDimensions,itemUnitOfMeasures" : "&$expand=itemUnitOfMeasures"), "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
    }

    public static async Task<HttpResponseMessage?> GetItemAsync(HttpClient client, IConfigurationRoot config, string? company = null, string? itemCode = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string json = ConvertCsvToItemJson(config, company, itemCode, logger).First();
        return await ItemBCRequest.UpsertItemAsync(client, config, company, json, null, logger, authHelper, cancellationToken);
    }

    public static async Task<HttpResponseMessage?> SyncItemsAsync(HttpClient client, IConfigurationRoot config, string company, Dictionary<string, Dictionary<string, string>>? allItemData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string json = ConvertCsvToItemJson(config, company, null, logger).First();
        return await ItemBCRequest.UpsertItemAsync(client, config, company, json, allItemData, logger, authHelper, cancellationToken);
    }

    public static async Task<string> ProcessItemsAsync(HttpClient client, IConfigurationRoot config, string? company = null, Dictionary<string, Dictionary<string, string>>? allItemData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        var stopwatchSuppliers = Stopwatch.StartNew();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Start processing items for company '{company}'.");
        foreach (string json in ConvertCsvToItemJson(config, company, null, logger))
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
    public static IEnumerable<string> ConvertCsvToItemJson(IConfigurationRoot config, string? company = null, string? itemNumber = null, EventLog? logger = null)
    {
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
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
        bool useDefaultDimensions = bool.TryParse(config[$"Companies:{company}:ItemData:UseDefaultDimension"], out bool dimensions) ? dimensions : false;

        var bcVatBusPostingGroupMapping = GetBcVatBusPostingGroupMapping(config, company);

        Encoding encoding = StringHelper.GetEncoding(encodingName);
        List<string> unknowItemList = [];

        using var sr = new StreamReader(csvPath, encoding);
        string? headerLine = sr.ReadLine();
        if (headerLine == null)
            yield break;

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

                string? no = "";
                // 'kode' appears to be the product code used in the CSV -> map to 'no'
                if (DictionaryHelper.TryGet(map, "kode", out no) && !string.IsNullOrWhiteSpace(no))
                {
                    no = itemNumberPrefix + no;
                    item["no"] = no;
                    // MUST BE SPECIFIC ITEM NUMBER
                    if (!string.IsNullOrWhiteSpace(itemNumber) && !no.Equals(itemNumber, StringComparison.InvariantCultureIgnoreCase))
                        continue;
                }

                if (string.IsNullOrWhiteSpace(no))
                {
                    if (!string.IsNullOrWhiteSpace(itemNumber))
                    {
                        if (unknowItemList.Contains(itemNumber))
                        {
                            logger?.WarningAsync(EventLog.GetMethodName(), company, $"Item {itemNumber} not found in file, but already flagged").Wait();
                            continue;
                        }

                        unknowItemList.Add(itemNumber);

                        logger?.ErrorAsync(EventLog.GetMethodName(), company, new Exception($"Item {itemNumber} not found in file")).Wait();
                    }
                    else
                    {
                        logger?.WarningAsync(EventLog.GetMethodName(), company, $"Skipping malformed CSV line, no item code {line}").Wait();
                    }

                    continue;
                }

                // displayName / description
                if (DictionaryHelper.TryGet(map, "omschr", out string? description) && !string.IsNullOrWhiteSpace(description))
                {
                    if (string.IsNullOrWhiteSpace(itemNumber))
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

                string? salesUnitOfMeasureCode = "";
                string? purchaseUnitOfMeasureCode = "";
                string? tradeUnitOfMeasureCode = "";

                // unit of measure
                if (DictionaryHelper.TryGet(map, "eenheid", out string? baseUnitOfMeasureCode) && !string.IsNullOrWhiteSpace(baseUnitOfMeasureCode))
                {
                    baseUnitOfMeasureCode = UomHelper.GetBcUom(company, baseUnitOfMeasureCode);
                    salesUnitOfMeasureCode = baseUnitOfMeasureCode;
                    purchaseUnitOfMeasureCode = baseUnitOfMeasureCode;
                    tradeUnitOfMeasureCode = baseUnitOfMeasureCode;
                }
                if (string.IsNullOrWhiteSpace(baseUnitOfMeasureCode))
                    baseUnitOfMeasureCode = config[$"Companies:{company}:ItemData:BasicUnitOfMeasureDefault"] ?? "STUKS";
                if (string.IsNullOrWhiteSpace(salesUnitOfMeasureCode))
                    salesUnitOfMeasureCode = config[$"Companies:{company}:ItemData:SalesUnitOfMeasureDefault"] ?? "STUKS";
                if (string.IsNullOrWhiteSpace(purchaseUnitOfMeasureCode))
                    purchaseUnitOfMeasureCode = config[$"Companies:{company}:ItemData:PurchaseUnitOfMeasureDefault"] ?? "STUKS";
                if (string.IsNullOrWhiteSpace(tradeUnitOfMeasureCode))
                    tradeUnitOfMeasureCode = config[$"Companies:{company}:ItemData:TradeUnitOfMeasureDefault"] ?? "STUKS";

                item["baseUnitOfMeasure"] = baseUnitOfMeasureCode;
                item["salesUnitOfMeasure"] = salesUnitOfMeasureCode;
                item["purchUnitOfMeasure"] = purchaseUnitOfMeasureCode;
                item["tradeUnitOfMeasure"] = tradeUnitOfMeasureCode;

                // barcode / ean -> map to gtin if available
                if (DictionaryHelper.TryGet(map, "barcode", out string? barcode) && !string.IsNullOrWhiteSpace(barcode))
                {
                    // sanitize non-digits
                    string digits = BiebuyckHelper.MyRegex().Replace(barcode, "");
                    if (!string.IsNullOrWhiteSpace(digits))
                        item["gtin"] = digits;
                }
                else if (DictionaryHelper.TryGet(map, "ean", out string? ean) && !string.IsNullOrWhiteSpace(ean))
                {
                    string digits = BiebuyckHelper.MyRegex().Replace(ean, "");
                    if (!string.IsNullOrWhiteSpace(digits))
                        item["gtin"] = digits;
                }

                // vendor item number
                if (DictionaryHelper.TryGet(map, "artnr_lev", out string? vendorNo) && !string.IsNullOrWhiteSpace(vendorNo))
                    item["vendorItemNo"] = vendorNo;

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

    public static Dictionary<double, string> GetBcVatBusPostingGroupMapping(IConfiguration config, string company)
    {
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        ArgumentNullException.ThrowIfNull(config);
        string sectionPath = $"Companies:{company}:VatData:VatBusPostingGroupMapping";
        var vatDataSection = config.GetSection(sectionPath);

        if (!vatDataSection.Exists())
            throw new InvalidOperationException(
                $"Configuratiesectie '{sectionPath}' werd niet gevonden.");

        var dict = new Dictionary<double, string>();

        foreach (var child in vatDataSection.GetChildren())
        {
            // child.Key = sleutel in appsettings, child.Value = stringwaarde
            // Lege of null keys overslaan
            if (!string.IsNullOrWhiteSpace(child.Key))
            {
                StringHelper.TryParseDouble(child.Value, CultureInfo.CurrentCulture, out double taxRate);
                dict[taxRate] = child.Key ?? string.Empty;
            }
        }

        return dict;
    }
}
