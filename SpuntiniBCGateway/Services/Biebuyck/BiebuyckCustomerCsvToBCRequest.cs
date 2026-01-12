using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpuntiniBCGateway.Services;

public static partial class BiebuyckCustomerCsvToBCRequest
{
    public static async Task<Dictionary<string, Dictionary<string, string>>> GetCustomersAsync(HttpClient client, IConfigurationRoot config, string? company = null, string? filter = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");
        
        return await CustomerBCRequest.GetCustomersAsync(client, config, company, "no", filter, logger, authHelper, cancellationToken);
    }

    public static async Task<HttpResponseMessage?> GetCustomerListAsync(HttpClient client, IConfigurationRoot config, string? company = null, List<string>? customerCodeList = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string json = ConvertCsvToCustomerJson(config, company, customerCodeList, logger).First();
        return await CustomerBCRequest.UpsertCustomerAsync(client, config, company, json, null, logger, authHelper, cancellationToken);
    }

    public static async Task<HttpResponseMessage?> SyncCustomersAsync(HttpClient client, IConfigurationRoot config, string? company = null, Dictionary<string, Dictionary<string, string>>? allCustomerData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string json = ConvertCsvToCustomerJson(config, company, null, logger).First();
        return await CustomerBCRequest.UpsertCustomerAsync(client, config, company, json, allCustomerData, logger, authHelper, cancellationToken);
    }

    public static async Task<string> ProcessCustomersAsync(HttpClient client, IConfigurationRoot config, string? company = null, List<string>? customerCodeList = null, Dictionary<string, Dictionary<string, string>>? allCustomerData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        var stopwatchSuppliers = Stopwatch.StartNew();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Start processing suppliers for company '{company}'.");

        foreach (string json in ConvertCsvToCustomerJson(config, company, customerCodeList, logger))
        {
            try
            {
                var resp = await CustomerBCRequest.UpsertCustomerAsync(client, config, company, json, allCustomerData, logger, authHelper, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);
            }
        }
        stopwatchSuppliers.Stop();
        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Finished processing suppliers for company '{company}' in {StringHelper.GetDurationString(stopwatchSuppliers.Elapsed)}.");

        return "OK";
    }

    // Convert CSV rows to JSON request bodies suitable for Business Central customers API.
    // Maps common columns from the provided sample CSV to a minimal BC customer payload.
    public static IEnumerable<string> ConvertCsvToCustomerJson(IConfigurationRoot config, string? company = null, List<string>? customerCodeList = null, EventLog? logger = null)
    {
        string sourceType = config[$"Companies:{company}:CustomerData:SourceType"] ?? "CSV";

        if (!sourceType.Equals("CSV", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Source type '{sourceType}' is not supported for customer data import.");
        }

        string csvPath = config[$"Companies:{company}:CustomerData:Source"] ?? string.Empty;

        try
        {
            if (!File.Exists(csvPath))
                throw new FileNotFoundException("CSV file not found", csvPath);
        }
        catch (Exception)
        {
            throw new Exception($"CSV file not found at: {csvPath}");
        }

        string customerNumberPrefix = config[$"Companies:{company}:CustomerData:CustomerNumberPrefix"] ?? "SPBI25";
        string csvDelimiter = config[$"Companies:{company}:CustomerData:Delimiter"] ?? ",";
        string encodingName = config[$"Companies:{company}:CustomerData:SourceEncoding"] ?? "UTF8";
        string systemFrench = config[$"Companies:{company}:BusinessCentral:FrenchLanguageCode"] ?? "FRB";
        string defaultSystemCountryCode = config[$"Companies:{company}:BusinessCentral:DefaultSystemCountryCode"] ?? "BE";
        string defaultSystemLanguageCode = config[$"Companies:{company}:BusinessCentral:DefaultSystemLanguageCode"] ?? "NLB";
        bool skipDuplicateCheck = bool.TryParse(config["$Companies:{company}:CustomerData:SkipDuplicateCheck"], out bool skip) && skip;
        List<string> manualInvoicedCodes = config[$"Companies:{company}:CustomerData:ManualInvoicedCodes"]?.Split(',')?.ToList() ?? [];
        List<string> dailyInvoicedCodes = config[$"Companies:{company}:CustomerData:DailyInvoicedCodes"]?.Split(',')?.ToList() ?? [];
        List<string> weeklyInvoicedCodes = config[$"Companies:{company}:CustomerData:WeeklyInvoicedCodes"]?.Split(',')?.ToList() ?? [];
        List<string> monthlyInvoicedCodes = config[$"Companies:{company}:CustomerData:MonthlyInvoicedCodes"]?.Split(',')?.ToList() ?? [];

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

                // Map known CSV columns to Business Central customer fields (minimal set)
                var customer = new Dictionary<string, object>();

                if (DictionaryHelper.TryGet(map, "nummer", out string? number))
                {
                    if (string.IsNullOrWhiteSpace(number))
                    {
                        continue; // skip rows without customer number)
                    }

                    number = customerNumberPrefix + number;

                    // MUST BE SPECIFIC CUSTOMER NUMBER
                    if (customerCodeList != null && customerCodeList.Count > 0 && !customerCodeList.Contains(number))
                        continue;

                    customer["no"] = number;
                }

                if (string.IsNullOrWhiteSpace(number))
                    continue;
                
                // Avoid writing logs for each line from same order ==> slows the proces down. So always safe last skipped docNum.
                if (!string.IsNullOrWhiteSpace(actualSkipped) && actualSkipped.Equals(number))
                    continue;

                actualSkipped = number;

                if (DictionaryHelper.TryGet(map, "aktief", out string? active))
                {
                    if (!string.IsNullOrWhiteSpace(active) && active.Equals("N", StringComparison.OrdinalIgnoreCase) && (customerCodeList == null || customerCodeList.Count <= 0))
                    {
                        logger?.InfoAsync(EventLog.GetMethodName(), company, $"Skipping inactive customer: {customer["no"]}").Wait();
                        continue;
                    }
                }

                if (DictionaryHelper.TryGet(map, "lnaam", out string? name) && !string.IsNullOrWhiteSpace(name))
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

                if (DictionaryHelper.TryGet(map, "naam", out string? name2) && !string.IsNullOrWhiteSpace(name2))
                {
                    name2 = StringHelper.CleanUpString(name2);
                    if (!string.IsNullOrWhiteSpace(name2))
                    {
                        if (!customer.ContainsKey("name"))
                            customer["name"] = name2;
                        else
                            customer["name2"] = name2;
                    }
                }

                if (DictionaryHelper.TryGet(map, "straat", out string? street) && !string.IsNullOrWhiteSpace(street))
                    customer["address"] = street;

                if (DictionaryHelper.TryGet(map, "stad", out string? city) && !string.IsNullOrWhiteSpace(city))
                    customer["city"] = city;

                if (DictionaryHelper.TryGet(map, "land", out string? country))
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

                if (DictionaryHelper.TryGet(map, "postnr", out string? zipCode) && !string.IsNullOrWhiteSpace(zipCode))
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

                if (DictionaryHelper.TryGet(map, "taal", out string? language))
                {
                    customer["languageCode"] = LanguageSanitizer.GetBcLangageCode(language, defaultSystemLanguageCode, systemFrench);
                }
                else
                {
                    customer["languageCode"] = defaultSystemLanguageCode; // default
                }

                if (DictionaryHelper.TryGet(map, "tel1", out string? phone) && !string.IsNullOrWhiteSpace(phone))
                    customer["phoneNo"] = MyRegex().Replace(phone, "");

                if (DictionaryHelper.TryGet(map, "email", out string? email) && !string.IsNullOrWhiteSpace(email))
                    customer["eMail"] = EmailSanitizer.CleanEmail(email);

                if (DictionaryHelper.TryGet(map, "btwnr", out string? vatNr) && !string.IsNullOrWhiteSpace(vatNr) && vatNr.Length > 6)
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
                                customer["enterpriseNo"] = vatNr[2..].Trim();
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

                if (DictionaryHelper.TryGet(map, "fdfkode", out string? assemblyFrequency))
                {
                    assemblyFrequency = assemblyFrequency?.Trim();
                }

                if (string.IsNullOrWhiteSpace(assemblyFrequency))
                {
                    assemblyFrequency = config[$"Companies:{company}:CustomerData:AssemblyFrequencyDefault"] ?? "Daily";
                }

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

                // if (DictionaryHelper.TryGet(map, "korting", out string? lineDiscountPercent) && decimal.TryParse(lineDiscountPercent, out decimal discountPercent))
                // {
                //     customer["lineDiscountPercent"] = discountPercent;
                // }

                // if (DictionaryHelper.TryGet(map, "kort_cont", out string? directPayDiscount) && decimal.TryParse(directPayDiscount, out decimal discount))
                // {
                //     customer["directPayDiscount"] = discount;
                // }

                if (DictionaryHelper.TryGet(map, "lman", out string? deliveryMonday) && !string.IsNullOrWhiteSpace(deliveryMonday))
                    customer["deliveryMonday"] = deliveryMonday.Equals("1", StringComparison.OrdinalIgnoreCase);
                if (DictionaryHelper.TryGet(map, "ldin", out string? deliveryTuesday) && !string.IsNullOrWhiteSpace(deliveryTuesday))
                    customer["deliveryTuesday"] = deliveryTuesday.Equals("1", StringComparison.OrdinalIgnoreCase);
                if (DictionaryHelper.TryGet(map, "lwoe", out string? deliveryWednesday) && !string.IsNullOrWhiteSpace(deliveryWednesday))
                    customer["deliveryWednesday"] = deliveryWednesday.Equals("1", StringComparison.OrdinalIgnoreCase);
                if (DictionaryHelper.TryGet(map, "ldon", out string? deliveryThursday) && !string.IsNullOrWhiteSpace(deliveryThursday))
                    customer["deliveryThursday"] = deliveryThursday.Equals("1", StringComparison.OrdinalIgnoreCase);
                if (DictionaryHelper.TryGet(map, "lvri", out string? deliveryFriday) && !string.IsNullOrWhiteSpace(deliveryFriday))
                    customer["deliveryFriday"] = deliveryFriday.Equals("1", StringComparison.OrdinalIgnoreCase);
                if (DictionaryHelper.TryGet(map, "lzat", out string? deliverySaturday) && !string.IsNullOrWhiteSpace(deliverySaturday))
                    customer["deliverySaturday"] = deliverySaturday.Equals("1", StringComparison.OrdinalIgnoreCase);
                if (DictionaryHelper.TryGet(map, "lzon", out string? deliverySunday) && !string.IsNullOrWhiteSpace(deliverySunday))
                    customer["deliverySunday"] = deliverySunday.Equals("1", StringComparison.OrdinalIgnoreCase);

                // Add the company type inference: if first column (titel) contains PRIVAAT, set type
                //   if (DictionaryHelper.TryGet(map, "titel", out var titel) && titel?.IndexOf("PRIVAAT", StringComparison.OrdinalIgnoreCase) >= 0)
                //       customer["type"] = "Person"; // Business Central uses Person/Company
                //   else
                //       customer["type"] = "Company";

                // SET TRUE IF YOU WANT DOUBLE TAV NUMBERS ARE ALLOWED
                customer["skipDuplicateCheck"] = skipDuplicateCheck;

                // DEFAULT VALUES BC
                customer["documentSendingProfile"] = config[$"Companies:{company}:CustomerData:DocumentSendingProfileDefault"] ?? "BOCOUNT PRINT";
                customer["customerPostingGroup"] = config[$"Companies:{company}:CustomerData:CustomerPostingGroupDefault"] ?? "NORMAAL";
                customer["locationCode"] = config[$"Companies:{company}:CustomerData:LocationCodeDefault"] ?? "DEERLIJK";
                customer["combineShipments"] = config[$"Companies:{company}:CustomerData:CombineShipmentsDefault"] ?? "true";
                customer["genBusPostingGroup"] = config[$"Companies:{company}:CustomerData:GenBusPostingGroupDefault"] ?? "BE";
                customer["vatBusPostingGroup"] = config[$"Companies:{company}:CustomerData:VatBusPostingGroupDefault"] ?? "BINNENL";
                customer["showInCompany"] = config[$"Companies:{company}:CustomerData:ShowInCompanyDefault"] ?? "SPBE";

                // Serialize to compact JSON suitable as a request body
                json = JsonSerializer.Serialize(customer, new JsonSerializerOptions { WriteIndented = false });
            }
            catch (Exception)
            {
                logger?.WarningAsync(EventLog.GetMethodName(), company, $"Skipping malformed CSV line: {line}").Wait();
                continue;
            }

            yield return json;
        }
    }

    [GeneratedRegex(@"\D")]
    private static partial Regex MyRegex();
}
