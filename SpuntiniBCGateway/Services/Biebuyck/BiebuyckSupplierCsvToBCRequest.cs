using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpuntiniBCGateway.Services;

public static partial class BiebuyckSupplierCsvToBCRequest
{
    public static async Task<Dictionary<string, Dictionary<string, string>>> GetSuppliersAsync(HttpClient client, IConfigurationRoot config, string? company = null, string? filter = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string supplierUrl = config[$"Companies:{company}:SupplierData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:SupplierData:DestinationApiUrl required in config");

        if (!string.IsNullOrWhiteSpace(filter))
        {
            supplierUrl += "&$filter=" + filter;
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
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string supplierUrl = config[$"Companies:{company}:SupplierData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:SupplierData:DestinationApiUrl required in config");
        if (string.IsNullOrWhiteSpace(supplierUrl))
            throw new ArgumentException($"Companies:{company}:SupplierData:DestinationApiUrl required in config");
        if (!string.IsNullOrWhiteSpace(filter))
        {
            supplierUrl += "&$filter=" + filter;
        }
       
        var supplierResult = await BcRequest.GetBcDataAsync(client, supplierUrl, "systemId", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
       
        if (supplierResult == null || supplierResult.Count == 0)
            return supplierResult;

        var commentSupplierResult = new Dictionary<string, Dictionary<string, string>>();
        foreach (var supplierData in supplierResult.Values)
        {
            if (supplierData != null && supplierData.TryGetValue("commentLines", out string? commentLine))
            {
                if (!string.IsNullOrWhiteSpace(commentLine))
                {
                    var commentData = JsonHelper.GetDataFromJsonString(commentLine, "comment");
                    if (commentData != null)
                    {
                        foreach(var commentCode in commentData.Keys)
                        {
                            commentSupplierResult[commentCode] = supplierData;
                        }
                    }
                }
            }
        }

        return commentSupplierResult;
    }

    public static async Task<HttpResponseMessage?> GetSupplierListAsync(HttpClient client, IConfigurationRoot config, string? company = null, List<string>? supplierCodeList = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string json = ConvertCsvToSupplierJson(config, company, supplierCodeList, logger).First();
        return await ItemBCRequest.UpsertItemAsync(client, config, company, json, null, logger, authHelper, cancellationToken);
    }

    public static async Task<HttpResponseMessage?> SyncSuppliersAsync(HttpClient client, IConfigurationRoot config, string? company = null, Dictionary<string, Dictionary<string, string>>? allSupplierData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        string json = ConvertCsvToSupplierJson(config, company, null, logger).First();
        return await ItemBCRequest.UpsertItemAsync(client, config, company, json, allSupplierData, logger, authHelper, cancellationToken);
    }

    public static async Task<string> ProcessSuppliersAsync(HttpClient client, IConfigurationRoot config, string? company = null, List<string>? supplierCodeList = null, Dictionary<string, Dictionary<string, string>>? allSupplierData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        var stopwatchSuppliers = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BIEBUYCK'");

        if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Start processing suppliers for company '{company}'.");
        foreach (string json in ConvertCsvToSupplierJson(config, company, supplierCodeList, logger))
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
    public static IEnumerable<string> ConvertCsvToSupplierJson(IConfigurationRoot config, string? company = null, List<string>? supplierCodeList = null, EventLog? logger = null)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BIEBUYCK", StringComparison.OrdinalIgnoreCase))
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

                var vendor = new Dictionary<string, object>();

                // Supplier number
                if (DictionaryHelper.TryGet(map, "nummer", out string? number))
                {
                    if (string.IsNullOrWhiteSpace(number))
                        continue;

                    // MUST BE SPECIFIC SUPPLIER NUMBER
                    if (supplierCodeList != null && supplierCodeList.Count > 0 && !supplierCodeList.Contains(number))
                        continue;

                    vendor["no"] = number;
                }

                if (string.IsNullOrWhiteSpace(number))
                    continue;
                
                // Avoid writing logs for each line from same order ==> slows the proces down. So always safe last skipped docNum.
                if (!string.IsNullOrWhiteSpace(actualSkipped) && actualSkipped.Equals(number))
                    continue;

                actualSkipped = number;

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
