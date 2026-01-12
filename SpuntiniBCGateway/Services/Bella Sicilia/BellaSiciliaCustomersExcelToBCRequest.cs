using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace SpuntiniBCGateway.Services;

public static partial class BellaSiciliaCustomersExcelToBCRequest
{
    public static async Task<Dictionary<string, Dictionary<string, string>>> GetCustomersAsync(HttpClient client, IConfigurationRoot config, string? company = null, string keyDefinition = "no", string filter = "", EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        return await CustomerBCRequest.GetCustomersAsync(client, config, company, keyDefinition, filter, logger, authHelper, cancellationToken);
    }

    public static async Task<HttpResponseMessage?> GetCustomerAsync(HttpClient client, IConfigurationRoot config, string? company = null, List<string>? customerCodeList = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        string json = ConvertExcelToCustomerJson(config, company, customerCodeList, logger).First();
        return await CustomerBCRequest.UpsertCustomerAsync(client, config, company, json, null, logger, authHelper, cancellationToken);
    }

    public static async Task<HttpResponseMessage?> SyncCustomersAsync(HttpClient client, IConfigurationRoot config, string? company = null, Dictionary<string, Dictionary<string, string>>? allCustomerData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        string json = ConvertExcelToCustomerJson(config, company, null, logger).First();
        return await CustomerBCRequest.UpsertCustomerAsync(client, config, company, json, allCustomerData, logger, authHelper, cancellationToken);
    }

    public static async Task<string> ProcessCustomersAsync(HttpClient client, IConfigurationRoot config, string? company = null, List<string>? customerCodeList = null, Dictionary<string, Dictionary<string, string>>? allCustomerData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        var stopwatchCustomers = Stopwatch.StartNew();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Start processing customers for company '{company}'.");
        foreach (string json in ConvertExcelToCustomerJson(config, company, customerCodeList, logger))
        {
            try
            {
                await CustomerBCRequest.UpsertCustomerAsync(client, config, company, json, allCustomerData, logger, authHelper, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
            }
        }
        stopwatchCustomers.Stop();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Finished processing customers for company '{company}' in {StringHelper.GetDurationString(stopwatchCustomers.Elapsed)}.");

        return "OK";
    }

    // Convert Excel rows to JSON request bodies suitable for Business Central customers API.
    // Maps common columns from the provided sample Excel file to a minimal BC customer payload.
    public static IEnumerable<string> ConvertExcelToCustomerJson(IConfigurationRoot config, string? company = null, List<string>? customerCodeList = null, EventLog? logger = null)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        string sourceType = config[$"Companies:{company}:CustomerData:SourceType"] ?? "EXCEL";

        if (!sourceType.Equals("XLSX", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Source type '{sourceType}' is not supported for customer data import.");
        }

        string excelPath = config[$"Companies:{company}:CustomerData:Source"] ?? string.Empty;

        try
        {
            if (!File.Exists(excelPath)) throw new FileNotFoundException("Excel file not found", excelPath);
        }
        catch (Exception)
        {
            throw new Exception("Excel file not found at: " + excelPath);
        }

        string worksheetName = config[$"Companies:{company}:CustomerData:WorksheetName"] ?? "Clients";
        string customerNumberPrefix = config[$"Companies:{company}:CustomerData:CustomerNumberPrefix"] ?? "SPBS25";
        string systemFrench = config[$"Companies:{company}:BusinessCentral:FrenchLanguageCode"] ?? "FRB";
        string defaultSystemCountryCode = config[$"Companies:{company}:BusinessCentral:DefaultSystemCountryCode"] ?? "BE";
        string defaultSystemLanguageCode = config[$"Companies:{company}:BusinessCentral:DefaultSystemLanguageCode"] ?? "FRB";
        bool skipDuplicateCheck = bool.TryParse(config[$"Companies:{company}:CustomerData:SkipDuplicateCheck"], out bool skip) && skip;
        List<string> manualInvoicedCodes = config[$"Companies:{company}:CustomerData:ManualInvoicedCodes"]?.Split(',')?.ToList() ?? [];
        List<string> dailyInvoicedCodes = config[$"Companies:{company}:CustomerData:DailyInvoicedCodes"]?.Split(',')?.ToList() ?? [];
        List<string> weeklyInvoicedCodes = config[$"Companies:{company}:CustomerData:WeeklyInvoicedCodes"]?.Split(',')?.ToList() ?? [];
        List<string> monthlyInvoicedCodes = config[$"Companies:{company}:CustomerData:MonthlyInvoicedCodes"]?.Split(',')?.ToList() ?? [];

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

                // Map known Excel columns to Business Central customer fields (minimal set)
                var customer = new Dictionary<string, object>();

                if (TryGetCellValue(row, headers, "IDThird", out string? number))
                {
                    if (string.IsNullOrWhiteSpace(number))
                    {
                        continue; // skip rows without customer number
                    }

                    number = customerNumberPrefix + number;

                    // MUST BE SPECIFIC CUSTOMER NUMBER
                    if (customerCodeList != null && customerCodeList.Count > 0 && !customerCodeList.Contains(number))
                        continue;

                    customer["no"] = number;
                }

                if (string.IsNullOrWhiteSpace(number))
                    continue;

                // Avoid writing logs for each line from same order ==> slows the proces down. So always save last skipped docNum.
                if (!string.IsNullOrWhiteSpace(actualSkipped) && actualSkipped.Equals(number))
                    continue;

                actualSkipped = number;

                if (TryGetCellValue(row, headers, "IsActive", out string? active))
                {
                    if (!string.IsNullOrWhiteSpace(active) && !StringHelper.IsTrue(active) && (customerCodeList == null || customerCodeList.Count <= 0))
                    {
                        logger?.InfoAsync(EventLog.GetMethodName(), company, $"Skipping inactive customer: {customer["no"]}").Wait();
                        continue;
                    }
                }

                if (TryGetCellValue(row, headers, "Name1", out string? name) && !string.IsNullOrWhiteSpace(name))
                {
                    name = StringHelper.CleanUpString(name);
                    if (!string.IsNullOrWhiteSpace(name))
                        customer["name"] = name;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    logger?.InfoAsync(EventLog.GetMethodName(), company, $"customer: {customer["no"]} has no name").Wait();
                    continue;
                }

                if (TryGetCellValue(row, headers, "Name2", out string? name2) && !string.IsNullOrWhiteSpace(name2))
                {
                    if (!string.IsNullOrWhiteSpace(name2))
                    {
                        name2 = StringHelper.CleanUpString(name2);

                        if (!name.Equals(name2))
                        {
                            if (!customer.ContainsKey("name"))
                                customer["name"] = name2;
                            else
                                customer["name2"] = name2;
                        }
                    }
                }

                if (TryGetCellValue(row, headers, "AddressRoad", out string? street) && !string.IsNullOrWhiteSpace(street))
                    customer["address"] = street;

                if (TryGetCellValue(row, headers, "AddressCity", out string? city) && !string.IsNullOrWhiteSpace(city))
                {
                    if (city.Contains(' ') && city.Length > 30)
                    {
                        var splittedCity = city.Split(" ", StringSplitOptions.RemoveEmptyEntries);

                        var tempCity = "";
                        foreach (var text in splittedCity)
                        {
                            if (text.Length > 30 || tempCity.Length + 1 + text.Length > 30)
                                continue;

                            if (tempCity.Length > 1) tempCity += " ";
                            tempCity += text;
                        }

                        city = tempCity;
                    }

                    if (city.Length > 30) city = city[..30];

                    if (!string.IsNullOrWhiteSpace(city))
                        customer["city"] = city;
                }

                if (TryGetCellValue(row, headers, "IDCountryAddress", out string? country))
                {
                    var countryIso2Code = CountrySanitizer.GetCountryIso2Code(country, defaultSystemCountryCode);
                    if (!string.IsNullOrWhiteSpace(countryIso2Code))
                    {
                        customer["countryRegionCode"] = countryIso2Code;
                    }
                    else
                        customer["countryRegionCode"] = defaultSystemCountryCode;
                }
                else
                    customer["countryRegionCode"] = defaultSystemCountryCode;

                if (TryGetCellValue(row, headers, "AddressZipCode", out string? zipCode) && !string.IsNullOrWhiteSpace(zipCode))
                {
                    if (customer["countryRegionCode"].Equals("NL") || customer["countryRegionCode"].Equals("IE"))
                    {
                        customer["postCode"] = zipCode;
                    }
                    else
                    {
                        customer["postCode"] = StringHelper.KeepOnlyNumbers(zipCode);
                    }
                }

                if (TryGetCellValue(row, headers, "Language", out string? language))
                {
                    customer["languageCode"] = LanguageSanitizer.GetBcLangageCode(language, defaultSystemLanguageCode, systemFrench);
                }
                else
                {
                    customer["languageCode"] = defaultSystemLanguageCode; // default
                }

                if (TryGetCellValue(row, headers, "Phone1", out string? phone) && !string.IsNullOrWhiteSpace(phone))
                    customer["phoneNo"] = MyRegex().Replace(phone, "");

                if (TryGetCellValue(row, headers, "Mail", out string? email) && !string.IsNullOrWhiteSpace(email))
                {
                    if (email.Contains(';'))
                    {
                        var splittedEmail = email.Split(";", StringSplitOptions.RemoveEmptyEntries);

                        var tempMail = "";
                        foreach (var text in splittedEmail)
                        {
                            var tempText = EmailSanitizer.CleanEmail(text);
                            if (!string.IsNullOrWhiteSpace(tempText))
                            {
                                if (tempText.Length > 80 || tempMail.Length + 1 + tempText.Length > 80)
                                    continue;

                                if (tempMail.Length > 1) tempMail += ";";
                                tempMail += tempText;
                            }
                        }

                        email = tempMail;
                    }
                    else
                    {
                        email = EmailSanitizer.CleanEmail(email);
                        if (email.Length > 80) email = "";
                    }

                    if (!string.IsNullOrWhiteSpace(email))
                        customer["eMail"] = email;
                }


                if (TryGetCellValue(row, headers, "VatNumber", out string? vatNr) && !string.IsNullOrWhiteSpace(vatNr) && vatNr.Length > 6)
                {
                    string countryCode = "";
                    customer.TryGetValue("countryRegionCode", out object? tempCountryCode);
                    if (tempCountryCode != null)
                    {
                        countryCode = tempCountryCode.ToString() ?? "";
                        if (defaultSystemCountryCode.Equals(countryCode, StringComparison.InvariantCultureIgnoreCase) && "BE".Equals(defaultSystemCountryCode))
                        {
                            if (BeVatValidator.IsValid(vatNr))
                            {
                                vatNr = VatSanitizer.Clean(vatNr);
                                customer["enterpriseNo"] = vatNr;
                            }
                        }
                        else if (EuVatValidator.TryValidate(vatNr, out vatNr, countryCode))
                        {
                            customer["vatRegistrationNo"] = vatNr;
                        }
                    }
                    else
                    {
                        customer["vatRegistrationNo"] = vatNr;
                    }
                }

                if (TryGetCellValue(row, headers, "AssemblyFrequency", out string? assemblyFrequency))
                    assemblyFrequency = assemblyFrequency?.Trim();

                if (string.IsNullOrWhiteSpace(assemblyFrequency))
                    assemblyFrequency = config[$"Companies:{company}:CustomerData:AssemblyFrequencyDefault"] ?? "Daily";

                if (manualInvoicedCodes.Contains(assemblyFrequency))
                {
                    customer["assemblyFrequency"] = "Manual";
                }
                else if (dailyInvoicedCodes.Contains(assemblyFrequency))
                {
                    customer["assemblyFrequency"] = "Daily";
                }
                else if (weeklyInvoicedCodes.Contains(assemblyFrequency))
                {
                    customer["assemblyFrequency"] = "Weekly";
                }
                else if (monthlyInvoicedCodes.Contains(assemblyFrequency))
                {
                    customer["assemblyFrequency"] = "Monthly";
                }
                else
                {
                    customer["assemblyFrequency"] = config[$"Companies:{company}:CustomerData:AssemblyFrequencyDefault"] ?? "Daily";
                }

                // if (TryGetCellValue(row, headers, "DeliveryMonday", out string? deliveryMonday) && !string.IsNullOrWhiteSpace(deliveryMonday))
                //     customer["deliveryMonday"] = StringHelper.IsTrue(deliveryMonday);
                // if (TryGetCellValue(row, headers, "DeliveryTuesday", out string? deliveryTuesday) && !string.IsNullOrWhiteSpace(deliveryTuesday))
                //     customer["deliveryTuesday"] = StringHelper.IsTrue(deliveryTuesday);
                // if (TryGetCellValue(row, headers, "DeliveryWednesday", out string? deliveryWednesday) && !string.IsNullOrWhiteSpace(deliveryWednesday))
                //     customer["deliveryWednesday"] = StringHelper.IsTrue(deliveryWednesday);
                // if (TryGetCellValue(row, headers, "DeliveryThursday", out string? deliveryThursday) && !string.IsNullOrWhiteSpace(deliveryThursday))
                //     customer["deliveryThursday"] = StringHelper.IsTrue(deliveryThursday);
                // if (TryGetCellValue(row, headers, "DeliveryFriday", out string? deliveryFriday) && !string.IsNullOrWhiteSpace(deliveryFriday))
                //     customer["deliveryFriday"] = StringHelper.IsTrue(deliveryFriday);
                // if (TryGetCellValue(row, headers, "DeliverySaturday", out string? deliverySaturday) && !string.IsNullOrWhiteSpace(deliverySaturday))
                //     customer["deliverySaturday"] = StringHelper.IsTrue(deliverySaturday);
                // if (TryGetCellValue(row, headers, "DeliverySunday", out string? deliverySunday) && !string.IsNullOrWhiteSpace(deliverySunday))
                //     customer["deliverySunday"] = StringHelper.IsTrue(deliverySunday);

                // SET TRUE IF YOU WANT DOUBLE TAV NUMBERS ARE ALLOWED
                customer["skipDuplicateCheck"] = skipDuplicateCheck;

                // DEFAULT VALUES BC
                customer["documentSendingProfile"] = config[$"Companies:{company}:CustomerData:DocumentSendingProfileDefault"] ?? "BOCOUNT PRINT";
                customer["customerPostingGroup"] = config[$"Companies:{company}:CustomerData:CustomerPostingGroupDefault"] ?? "NORMAAL";
                customer["locationCode"] = config[$"Companies:{company}:CustomerData:LocationCodeDefault"] ?? "LALOUVIERE";
                customer["combineShipments"] = config[$"Companies:{company}:CustomerData:CombineShipmentsDefault"] ?? "true";
                customer["genBusPostingGroup"] = config[$"Companies:{company}:CustomerData:GenBusPostingGroupDefault"] ?? "BE";
                customer["vatBusPostingGroup"] = config[$"Companies:{company}:CustomerData:VatBusPostingGroupDefault"] ?? "BINNENL";
                customer["showInCompany"] = config[$"Companies:{company}:CustomerData:ShowInCompanyDefault"] ?? "SPBS";

                // Serialize to compact JSON suitable as a request body
                json = JsonSerializer.Serialize(customer, new JsonSerializerOptions { WriteIndented = false });
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

    [GeneratedRegex(@"\D")]
    private static partial Regex MyRegex();
}
