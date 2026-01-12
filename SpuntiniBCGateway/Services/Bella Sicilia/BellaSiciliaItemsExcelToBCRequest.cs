using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;

namespace SpuntiniBCGateway.Services;

public static partial class BellaSiciliaItemsExcelToBCRequest
{
    public static async Task<Dictionary<string, Dictionary<string, string>>> GetItemsAsync(HttpClient client, IConfigurationRoot config, string? company = null, string filter = "", string expand = "", EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        return await ItemBCRequest.GetItemsAsync(client, config, company, filter, expand, logger, authHelper, cancellationToken);
    }

    public static async Task<HttpResponseMessage?> GetItemAsync(HttpClient client, IConfigurationRoot config, string? company = null, List<string>? itemNumberList = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        string json = ConvertExcelToItemJson(config, company, itemNumberList, logger).First();
        return await ItemBCRequest.UpsertItemAsync(client, config, company, json, null, logger, authHelper, cancellationToken);
    }

    public static async Task<HttpResponseMessage?> SyncItemsAsync(HttpClient client, IConfigurationRoot config, string? company = null, Dictionary<string, Dictionary<string, string>>? allItemData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        string json = ConvertExcelToItemJson(config, company, null, logger).First();
        return await ItemBCRequest.UpsertItemAsync(client, config, company, json, allItemData, logger, authHelper, cancellationToken);
    }

    public static async Task<string> ProcessItemsAsync(HttpClient client, IConfigurationRoot config, string? company = null, List<string>? itemNumberList = null, Dictionary<string, Dictionary<string, string>>? allItemData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        var stopwatchItems = Stopwatch.StartNew();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Start processing items for company '{company}'.");
        foreach (string json in ConvertExcelToItemJson(config, company, itemNumberList, logger))
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
    public static IEnumerable<string> ConvertExcelToItemJson(IConfigurationRoot config, string? company = null, List<string>? itemNumberList = null, EventLog? logger = null)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        string sourceType = config[$"Companies:{company}:ItemData:SourceType"] ?? "EXCEL";

        if (!sourceType.Equals("XLSX", StringComparison.OrdinalIgnoreCase))
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
        string cartonUom = config[$"Companies:{company}:ItemData:CartonUnitOfMeasureDefault"] ?? "KARTON";
        bool useDefaultDimensions = bool.TryParse(config[$"Companies:{company}:ItemData:UseDefaultDimensions"], out bool dimensions) && dimensions;

        var bcVatBusPostingGroupMapping = GetBcVatBusPostingGroupMapping(config, company);

        using var workbook = new XLWorkbook(excelPath);

        var worksheet = workbook.Worksheets.FirstOrDefault(w => w.Name.Equals(worksheetName, StringComparison.OrdinalIgnoreCase)) ?? throw new Exception($"Worksheet '{worksheetName}' not found in Excel file.");
        var rows = worksheet.RangeUsed().Rows().ToList();
        if (rows.Count == 0)
            yield break;
        string? actualSkipped = null;
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

                if (!TryGetCellValue(row, headers, "#IDCarPassActivity", out var process))
                    continue;

                var item = new Dictionary<string, object>();

                // Get item code from the configured column
                if (TryGetCellValue(row, headers, "IDArticle", out string? no) && !string.IsNullOrWhiteSpace(no))
                {
                    no = BellaSiciliaHelper.GetBcItemNumberFromBellaSiciliaItemNumber(config, company, no);
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

                if (TryGetCellValue(row, headers, "IsActive", out string? isActive) && !StringHelper.IsTrue(isActive))
                {
                    if (itemNumberList == null || itemNumberList.Count <= 0)
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

                string? baseUnitOfMeasureCode = config[$"Companies:{company}:ItemData:BasicUnitOfMeasureDefault"] ?? "STUKS";
                item["baseUnitOfMeasure"] = baseUnitOfMeasureCode;
                item["salesUnitOfMeasure"] = config[$"Companies:{company}:ItemData:SalesUnitOfMeasureDefault"] ?? baseUnitOfMeasureCode;
                item["purchUnitOfMeasure"] = config[$"Companies:{company}:ItemData:PurchaseUnitOfMeasureDefault"] ?? baseUnitOfMeasureCode;

                // unit of measure
                if (TryGetCellValue(row, headers, "IDStorageUnit", out string? tradeUnitOfMeasureCode) && !string.IsNullOrWhiteSpace(tradeUnitOfMeasureCode))
                    tradeUnitOfMeasureCode = UomHelper.GetBcUom(company, tradeUnitOfMeasureCode);

                if (string.IsNullOrWhiteSpace(tradeUnitOfMeasureCode))
                    tradeUnitOfMeasureCode = config[$"Companies:{company}:ItemData:TradeUnitOfMeasureDefault"] ?? "STUK";

                item["tradeUnitOfMeasure"] = tradeUnitOfMeasureCode;

                // VAT
                if (TryGetCellValue(row, headers, "IDVatRate", out string? vatPercentage) && StringHelper.TryParseDouble(vatPercentage, CultureInfo.CurrentCulture, out double taxRate) && bcVatBusPostingGroupMapping.TryGetValue(taxRate, out string? bcTaxCode) && !string.IsNullOrWhiteSpace(bcTaxCode))
                    item["vatProdPostingGroup"] = bcTaxCode;

                double weightGram = 0d;

                if (TryGetCellValue(row, headers, "Weight", out string? weightParameter) && !string.IsNullOrWhiteSpace(weightParameter) && double.TryParse(weightParameter.Replace(',', '.'), out var weight))
                {
                    if (weight > 0d) weightGram = weight * 1000;
                }

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

                var qtyBasePerCarton = 1;

                if (TryGetCellValue(row, headers, "PackagingQuantity", out string? cartonQty) && !string.IsNullOrWhiteSpace(cartonQty) && int.TryParse(cartonQty, out qtyBasePerCarton) && qtyBasePerCarton > 1)
                {
                    uomObjectList.Add(new Dictionary<string, object>
                        {
                            {"itemNo", no},
                            {"code", cartonUom},
                            {"qtyPerUnitOfMeasure", qtyBasePerCarton},
                            {"qtyRoundingPrecision", 0},
                            {"weight", weightGram * qtyBasePerCarton},
                        });
                }

                if (uomObjectList.Count > 0)
                    item["itemUnitOfMeasures"] = uomObjectList;

                // barcode / ean -> map to gtin if available
                List<string> eanBarcodes = [];
                var eanBarcodeList = new List<object>();

                if (TryGetCellValue(row, headers, "EAN13", out string? barcode) && !string.IsNullOrWhiteSpace(barcode) && GtinValidator.IsValidGtin(barcode, out _) && !eanBarcodes.Contains(barcode))
                {
                    eanBarcodes.Add(barcode);

                    eanBarcodeList.Add(new Dictionary<string, object>
                            {
                                {"itemNo", no},
                                {"referenceType", ItemReferencesBCRequest.ReferenceType_BarCode},
                                {"referenceNo", barcode},
                                {"unitOfMeasure", tradeUnitOfMeasureCode}
                            });
                }

                if (TryGetCellValue(row, headers, "EANCarton", out string? barcodeCart) && !string.IsNullOrWhiteSpace(barcodeCart) && GtinValidator.IsValidGtin(barcodeCart, out _) && !eanBarcodes.Contains(barcodeCart))
                {
                    eanBarcodes.Add(barcodeCart);

                    var uom = qtyBasePerCarton > 1 ? cartonUom : tradeUnitOfMeasureCode;
                    eanBarcodeList.Add(new Dictionary<string, object>
                            {
                                {"itemNo", no},
                                {"referenceType", ItemReferencesBCRequest.ReferenceType_BarCode},
                                {"referenceNo", barcodeCart},
                                {"unitOfMeasure", uom}
                            });
                }

                if (eanBarcodes.Count > 0)
                {
                    item["gtin"] = eanBarcodes[0];

                    item["itemReferences"] = eanBarcodeList;
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
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
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
