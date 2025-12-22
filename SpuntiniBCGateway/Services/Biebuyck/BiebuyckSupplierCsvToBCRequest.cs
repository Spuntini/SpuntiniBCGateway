using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpuntiniBCGateway.Services;

public static partial class BiebuyckSupplierCsvToBCRequest
{
    public static async Task<Dictionary<string, Dictionary<string, string>>> GetSuppliersAsync(HttpClient client, IConfigurationRoot config, string? company = null, string? filter = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");
            
        string supplierUrl = config[$"Companies:{company}:SupplierData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:SupplierData:DestinationApiUrl required in config");
                
        if (!string.IsNullOrWhiteSpace(filter))
        {
            supplierUrl += "&$filter=" + Uri.EscapeDataString(filter);
        }
        else
        {
            supplierUrl += config[$"Companies:{company}:SupplierData:SelectAllFilter"] ?? "";
        }

        if (string.IsNullOrWhiteSpace(filter))
            return await BcRequest.GetBcDataAsync(client, supplierUrl + "?$expand=commentLines", "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);

        return await BcRequest.GetBcDataAsync(client, supplierUrl + filter + "&$expand=commentLines", "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
    }

    public static async Task<Dictionary<string, Dictionary<string, string>>?> GetSuppliersByCommentAsync(HttpClient client, IConfigurationRoot config, string? company = null, string? filter = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string commentUrl = config[$"Companies:{company}:CommentLineData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:CommentLineData:DestinationApiUrl required in config");
        if (string.IsNullOrWhiteSpace(commentUrl))
            throw new ArgumentException($"Companies:{company}:CommentLineData:DestinationApiUrl required in config");
        
        string supplierUrl = config[$"Companies:{company}:SupplierData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:SupplierData:DestinationApiUrl required in config");
        if (string.IsNullOrWhiteSpace(supplierUrl))
            throw new ArgumentException($"Companies:{company}:SupplierData:DestinationApiUrl required in config"); 
        if (!string.IsNullOrWhiteSpace(filter))
        {
            commentUrl += "&$filter=" + Uri.EscapeDataString(filter);
        }
        else
        {
            commentUrl += config[$"Companies:{company}:CommentLineData:SelectAllFilter"] ?? "";
        }

        Dictionary<string, Dictionary<string, string>> tempResult;
        if (string.IsNullOrWhiteSpace(filter))
            tempResult = await BcRequest.GetBcDataAsync(client, commentUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
        else
            tempResult = await BcRequest.GetBcDataAsync(client, commentUrl + filter, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);

        if (tempResult == null || tempResult.Count == 0)
            return tempResult;

        var tempResultBySupplier = new Dictionary<string, Dictionary<string, string>>();
        foreach (var item in tempResult)
        {
            if (item.Value != null && item.Value.TryGetValue("comment", out string? supplierNo))
            {
                if (!string.IsNullOrWhiteSpace(supplierNo))
                {
                    item.Value.TryGetValue("no", out string? no);

                    if (!tempResultBySupplier.ContainsKey(supplierNo) && !string.IsNullOrWhiteSpace(no))
                    {
                        var supplierData = await BcRequest.GetBcDataAsync(client, supplierUrl + $"?$filter=no eq '{no.Replace("'", "''")}'", "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);

                        tempResultBySupplier[supplierNo] = supplierData[no];
                    }
                }
            }
        }

        return tempResultBySupplier;
    }


    public static async Task<HttpResponseMessage?> GetSupplierAsync(HttpClient client, IConfigurationRoot config, string? company = null, string? supplierCode = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string json = ConvertCsvToSupplierJson(config, company, supplierCode, logger).First();
        return await ItemBCRequest.UpsertItemAsync(client, config, company, json, null, logger, authHelper, cancellationToken);
    }

    public static async Task<HttpResponseMessage?> SyncSuppliersAsync(HttpClient client, IConfigurationRoot config, string? company = null, Dictionary<string, Dictionary<string, string>>? allSupplierData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string json = ConvertCsvToSupplierJson(config, company, company, logger).First();
        return await ItemBCRequest.UpsertItemAsync(client, config, company, json, allSupplierData, logger, authHelper, cancellationToken);
    }

    public static async Task<string> ProcessSuppliersAsync(HttpClient client, IConfigurationRoot config, string? company = null, Dictionary<string, Dictionary<string, string>>? allSupplierData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        var stopwatchSuppliers = Stopwatch.StartNew();
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Start processing suppliers for company '{company}'.");
        foreach (string json in ConvertCsvToSupplierJson(config, company, null, logger))
        {
            try
            {
                var resp = await SupplierBCRequest.UpsertSupplierAsync(client, config, company, json, allSupplierData, logger, authHelper, cancellationToken).ConfigureAwait(false);
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

    // Convert CSV rows to JSON request bodies suitable for Business Central vendors (suppliers) API.
    // Maps common columns from the provided sample CSV to a minimal BC vendor payload.
    public static IEnumerable<string> ConvertCsvToSupplierJson(IConfigurationRoot config, string? company = null, string? supplierCode = null, EventLog? logger = null)
    {
        if(string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string sourceType = config[$"Companies:{company}:SupplierData:SourceType"] ?? "CSV";

        if (!sourceType.Equals("CSV", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Source type '{sourceType}' is not supported for customer data import.");
        }

        string csvPath = config[$"Companies:{company}:SupplierData:Source"] ?? string.Empty;

        try
        {
            if (!File.Exists(csvPath))
                throw new FileNotFoundException("CSV file not found", csvPath);
        }
        catch (Exception)
        {
            throw new Exception("CSV file not found at: " + csvPath);
        }

        string csvDelimiter = config[$"Companies:{company}:SupplierData:Delimiter"] ?? ",";
        string encodingName = config[$"Companies:{company}:SupplierData:SourceEncoding"] ?? "UTF8";
        string systemFrench = config[$"Companies:{company}:BusinessCentral:FrenchLanguageCode"] ?? "FRB";
        string defaultSystemCountryCode = config[$"Companies:{company}:BusinessCentral:DefaultSystemCountryCode"] ?? "BE";
        string defaultSystemLanguageCode = config[$"Companies:{company}:BusinessCentral:DefaultSystemLanguageCode"] ?? "NLB";

        Encoding encoding = StringHelper.GetEncoding(encodingName);

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

                var vendor = new Dictionary<string, object>();

                string? number = "";
                // Supplier number
                if (DictionaryHelper.TryGet(map, "nummer", out number))
                {
                    if (string.IsNullOrWhiteSpace(number))
                        continue;

                    // MUST BE SPECIFIC SUPPLIER NUMBER
                    if (!string.IsNullOrWhiteSpace(supplierCode) && number.Equals(supplierCode, StringComparison.InvariantCultureIgnoreCase))
                        continue;

                    vendor["no"] = number;
                }

                if (string.IsNullOrWhiteSpace(number))
                {
                    if (!string.IsNullOrWhiteSpace(supplierCode))
                    {
                        logger?.ErrorAsync(EventLog.GetMethodName(), company, new Exception($"Supplier {supplierCode} not found in file")).Wait();
                    }
                    else
                    {
                        logger?.WarningAsync(EventLog.GetMethodName(), company, $"Skipping malformed CSV line, no supplier code {line}").Wait();
                    }

                    continue;
                }

                // Name / display
                if (DictionaryHelper.TryGet(map, "naam", out string? name) && !string.IsNullOrWhiteSpace(name))
                    vendor["name"] = name;

                if (DictionaryHelper.TryGet(map, "straat", out string? street) && !string.IsNullOrWhiteSpace(street))
                    vendor["address"] = street;

                if (DictionaryHelper.TryGet(map, "stad", out string? city) && !string.IsNullOrWhiteSpace(city))
                    vendor["city"] = city;

                if (DictionaryHelper.TryGet(map, "land", out string? country))
                {
                    var countryIso2Code = CountrySanitizer.GetCountryIso2Code(country, defaultSystemCountryCode);
                    if (!string.IsNullOrWhiteSpace(countryIso2Code))
                    {
                        vendor["countryRegionCode"] = countryIso2Code;
                    }
                    else
                        vendor["countryRegionCode"] = defaultSystemCountryCode;
                }
                else
                    vendor["countryRegionCode"] = defaultSystemCountryCode;

                if (DictionaryHelper.TryGet(map, "postnr", out string? zipCode) && !string.IsNullOrWhiteSpace(zipCode))
                {
                    if (vendor["countryRegionCode"].Equals("NL") || vendor["countryRegionCode"].Equals("IE"))
                    {
                        vendor["postCode"] = zipCode;
                    }
                    else
                    {
                        vendor["postCode"] = StringHelper.KeepOnlyNumbers(zipCode);
                    }
                }

                if (DictionaryHelper.TryGet(map, "taal", out string? language))
                {
                    vendor["languageCode"] = LanguageSanitizer.GetBcLangageCode(language, defaultSystemLanguageCode, systemFrench);
                }
                else
                {
                    vendor["languageCode"] = defaultSystemLanguageCode; // default
                }

                if (DictionaryHelper.TryGet(map, "tel1", out string? phoneNumber) && !string.IsNullOrWhiteSpace(phoneNumber))
                    vendor["phoneNumber"] = MyRegex().Replace(phoneNumber, "");

                if (DictionaryHelper.TryGet(map, "email", out string? email) && !string.IsNullOrWhiteSpace(email))
                    vendor["email"] = EmailSanitizer.CleanEmail(email);

                if (DictionaryHelper.TryGet(map, "btwnr", out string? vatNr) && !string.IsNullOrWhiteSpace(vatNr) && vatNr.Length > 6)
                {
                    string countryCode = "";
                    vendor.TryGetValue("countryRegionCode", out object? tempCountryCode);
                    if (tempCountryCode != null)
                    {
                        countryCode = tempCountryCode.ToString() ?? "";
                        if (defaultSystemCountryCode.Equals(countryCode, StringComparison.InvariantCultureIgnoreCase) && "BE".Equals(defaultSystemCountryCode))
                        {
                            if (BeVatValidator.IsValid(vatNr))
                            {
                                vatNr = VatSanitizer.Clean(vatNr, "BE");
                                vendor["enterpriseNo"] = vatNr[2..].Trim();
                            }
                        }
                        else if (EuVatValidator.TryValidate(vatNr, out vatNr, countryCode))
                        {
                            vendor["vatRegistrationNo"] = vatNr;
                        }
                    }
                    else
                    {
                        vendor["vatRegistrationNo"] = vatNr;
                    }
                }

                // DEFAULT VALUES BC                   
                vendor["genBusPostingGroup"] = config[$"Companies:{company}:SupplierData:GenBusPostingGroupDefault"] ?? "BE";
                vendor["vatBusPostingGroup"] = config[$"Companies:{company}:SupplierData:VatBusPostingGroupDefault"] ?? "BINNENL";

                json = JsonSerializer.Serialize(vendor, new JsonSerializerOptions { WriteIndented = false });
            }
            catch (Exception)
            {
                logger?.WarningAsync(EventLog.GetMethodName(), company, $"Skipping malformed CSV line: {line}").Wait();
                continue;
            }

            yield return json;
        }
    }

    [GeneratedRegex("\\D")]
    private static partial Regex MyRegex();
}
