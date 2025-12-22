using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;

namespace SpuntiniBCGateway.Services;

public static partial class BellaSiciliaItemsExcelToBCRequest
{
    public static async Task<Dictionary<string, Dictionary<string, string>>> GetItemsAsync(HttpClient client, IConfigurationRoot config, string? company = null, string filter = "", EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

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
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        string json = ConvertExcelToItemJson(config, company, itemCode, logger).First();
        return await ItemBCRequest.UpsertItemAsync(client, config, company, json, null, logger, authHelper, cancellationToken);
    }

    public static async Task<HttpResponseMessage?> SyncItemsAsync(HttpClient client, IConfigurationRoot config, string? company = null, Dictionary<string, Dictionary<string, string>>? allItemData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        string json = ConvertExcelToItemJson(config, company, null, logger).First();
        return await ItemBCRequest.UpsertItemAsync(client, config, company, json, allItemData, logger, authHelper, cancellationToken);
    }

    public static async Task<string> ProcessItemsAsync(HttpClient client, IConfigurationRoot config, string? company = null, Dictionary<string, Dictionary<string, string>>? allItemData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        var stopwatchItems = Stopwatch.StartNew();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Start processing items for company '{company}'.");
        foreach (string json in ConvertExcelToItemJson(config, company, null, logger))
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
        stopwatchItems.Stop();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Finished processing items for company '{company}' in {StringHelper.GetDurationString(stopwatchItems.Elapsed)}.");

        return "OK";
    }

    // Convert Excel rows to JSON request bodies suitable for Business Central items API.
    // Maps common columns from the provided sample Excel file to a minimal BC item payload.
    public static IEnumerable<string> ConvertExcelToItemJson(IConfigurationRoot config, string? company = null, string? itemNumber = null, EventLog? logger = null)
    {
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        string sourceType = config[$"Companies:{company}:ItemData:SourceType"] ?? "EXCEL";

        if (!sourceType.Equals("EXCEL", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Source type '{sourceType}' is not supported for item data import.");
        }

        string excelPath = config[$"Companies:{company}:ItemData:Source"] ?? string.Empty;

        try
        {
            if (!File.Exists(excelPath)) throw new FileNotFoundException("Excel file not found", excelPath);
        }
        catch (Exception)
        {
            throw new Exception("Excel file not found at: " + excelPath);
        }

        string worksheetName = config[$"Companies:{company}:ItemData:WorksheetName"] ?? "Articles";
        string itemNumberPrefix = config[$"Companies:{company}:ItemData:ItemNumberPrefix"] ?? "BS";
        bool useDefaultDimensions = bool.TryParse(config[$"Companies:{company}:ItemData:UseDefaultDimension"], out bool dimensions) ? dimensions : false;

        var bcVatBusPostingGroupMapping = GetBcVatBusPostingGroupMapping(config);

        using var workbook = new XLWorkbook(excelPath);
        
        var worksheet = workbook.Worksheets.FirstOrDefault(w => w.Name.Equals(worksheetName, StringComparison.OrdinalIgnoreCase));
        if (worksheet == null)
        {
            throw new Exception($"Worksheet '{worksheetName}' not found in Excel file.");
        }

        var rows = worksheet.RangeUsed().Rows().ToList();
        if (rows.Count == 0)
            yield break;

        // Get header row (first row)
        var headerRow = rows[0];
        var headers = headerRow.Cells()
            .Select((cell, index) => new { Index = index + 1, Value = cell.GetValue<string>()?.Trim() ?? string.Empty })
            .Where(h => !string.IsNullOrWhiteSpace(h.Value))
            .ToDictionary(h => h.Value, h => h.Index);

        // Process data rows (skip header)
        for (int rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            string json = string.Empty;
            try
            {
                var row = rows[rowIndex];

                if (TryGetCellValue(row, headers, "#IDCarPassActivity", out var process) && string.IsNullOrWhiteSpace(process))
                    continue;
                
                var item = new Dictionary<string, object>();

                string? no = "";
                // Get item code from the configured column
                if (TryGetCellValue(row, headers, "IDArticle", out no) && !string.IsNullOrWhiteSpace(no))
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
                        logger?.ErrorAsync(EventLog.GetMethodName(), company, new Exception($"Item {itemNumber} not found in file")).Wait();
                    }
                    else
                    {
                        logger?.WarningAsync(EventLog.GetMethodName(), company, $"Skipping malformed Excel row {rowIndex + 1}, no item code").Wait();
                    }

                    continue;
                }

                if (TryGetCellValue(row, headers, "IsActive", out string? isActive) && !StringHelper.IsTrue(isActive))
                {
                    if (!string.IsNullOrWhiteSpace(itemNumber) && itemNumber.Equals(no, StringComparison.InvariantCultureIgnoreCase))
                    {
                        logger?.InfoAsync(EventLog.GetMethodName(), company, $"Item {itemNumber} is inactive in source file.").Wait();
                    }
                    else
                    {
                        logger?.WarningAsync(EventLog.GetMethodName(), company, $"Skipping inactive item {no} at Excel row {rowIndex + 1}.").Wait();
                        continue;
                    }
                }
                
                if (TryGetCellValue(row, headers, "TitleFr", out string? description) && !string.IsNullOrWhiteSpace(description))
                {
                    item["description"] = StringHelper.CleanUpString(description.Trim());
                }
                else
                {
                    logger?.WarningAsync(EventLog.GetMethodName(), company, $"Skipping malformed Excel row {rowIndex + 1}, no item description").Wait();
                    continue;
                }

                string? salesUnitOfMeasureCode = "";
                string? purchaseUnitOfMeasureCode = "";
                string? tradeUnitOfMeasureCode = "";

                // unit of measure
                if (TryGetCellValue(row, headers, "IDStorageUnit", out string? baseUnitOfMeasureCode) && !string.IsNullOrWhiteSpace(baseUnitOfMeasureCode))
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
                if (TryGetCellValue(row, headers, "EAN13", out string? barcode) && !string.IsNullOrWhiteSpace(barcode))
                {
                    string digits = System.Text.RegularExpressions.Regex.Replace(barcode, "[^0-9]", "");
                    if (!string.IsNullOrWhiteSpace(digits))
                        item["gtin"] = digits;
                }
                else if (TryGetCellValue(row, headers, "ean", out string? ean) && !string.IsNullOrWhiteSpace(ean))
                {
                    string digits = System.Text.RegularExpressions.Regex.Replace(ean, "[^0-9]", "");
                    if (!string.IsNullOrWhiteSpace(digits))
                        item["gtin"] = digits;
                }                

                // VAT
                if (TryGetCellValue(row, headers, "sttel", out string? vatPercentage) && StringHelper.TryParseDouble(vatPercentage, CultureInfo.CurrentCulture, out double taxRate) && bcVatBusPostingGroupMapping.TryGetValue(taxRate, out string? bcTaxCode) && !string.IsNullOrWhiteSpace(bcTaxCode))
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
                    var dimension = DimensionBCRequest.GetItemCogsDimension(config, company, no);
                    item["defaultDimensions"] = new List<object>() { dimension };
                }

                // Serialize to compact JSON suitable as a request body
                json = JsonSerializer.Serialize(item, new JsonSerializerOptions { WriteIndented = false });
            }
            catch (Exception ex)
            {
                logger?.WarningAsync(EventLog.GetMethodName(), company, $"Skipping malformed Excel row {rowIndex + 1}: {ex.Message}").Wait();
                continue;
            }

            yield return json;
        }
    }

    // Helper method to safely get cell values from Excel row
    private static bool TryGetCellValue(IXLRangeRow row, Dictionary<string, int> headers, string columnName, out string? value)
    {
        value = null;
        
        if (!headers.TryGetValue(columnName, out int columnIndex))
            return false;

        try
        {
            var cell = row.Cell(columnIndex);
            if (cell == null)
                return false;

            value = cell.GetValue<string>();
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static Dictionary<double, string> GetBcVatBusPostingGroupMapping(IConfiguration config, string? company = null)
    {
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");
            
        ArgumentNullException.ThrowIfNull(config);
        string sectionPath = $"Companies:{company}:VatData:VatBusPostingGroupMapping";
        var vatDataSection = config.GetSection(sectionPath);

        if (!vatDataSection.Exists())
            throw new InvalidOperationException(
                $"Configuration section '{sectionPath}' was not found.");

        var dict = new Dictionary<double, string>();

        foreach (var child in vatDataSection.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Key))
            {
                StringHelper.TryParseDouble(child.Value, CultureInfo.CurrentCulture, out double taxRate);
                dict[taxRate] = child.Key ?? string.Empty;
            }
        }

        return dict;
    }
}
