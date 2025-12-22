using System.Diagnostics;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static class SupplierBCRequest
{
    // Attempts to find a vendor in Business Central by `number` or `vatRegistrationNumber`/`enterpriseNumber`.
    // If found, performs a PATCH to update the vendor; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage> UpsertSupplierAsync(HttpClient client, IConfigurationRoot config, string company, string supplierJson, Dictionary<string, Dictionary<string, string>>? allSupplierData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(company);
            ArgumentNullException.ThrowIfNull(allSupplierData);
            if (string.IsNullOrWhiteSpace(supplierJson)) throw new ArgumentException("supplierJson required", nameof(supplierJson));

            // The check if we know the supplier already is checking if there is a supplier with the SupplierNo in comment in BC linked to a supplier and the company as
            string commentLineUrl = config[$"Companies:{company}:CommentLineData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:CommentLineData:DestinationApiUrl required in config");
            string supplierUrl = config[$"Companies:{company}:SupplierData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:SupplierData:DestinationApiUrl required in config");

            // Parse provided JSON to extract number or vatRegistrationNumber for existence check
            using var doc = JsonDocument.Parse(supplierJson);
            var root = doc.RootElement;
            string? number = null;
            string? vat = null;
            string? enterpriseNumber = null;
            if (root.TryGetProperty("no", out var numProp) && numProp.ValueKind == JsonValueKind.String)
                number = numProp.GetString();

            ArgumentException.ThrowIfNullOrEmpty(number, "Supplier number (no) is required in supplierJson");

            // Build filter expression: prefer number, then enterpriseNumber, then vat
            string? commentFilter = null;
            string? supplierfilter = null;

            string escapedCompany = company.Replace("'", "''");
            commentFilter = $"comment eq '{number.Replace("'", "''")}' and code eq '{escapedCompany}'";

            string getUrl = commentLineUrl + "?$filter=" + Uri.EscapeDataString(commentFilter);

            var commentResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

            string? supplierId = string.Empty;

            Dictionary<string, string>? supplierData = null;
            if (commentResult != null && commentResult.TryGetValue("no", out supplierId) && !string.IsNullOrWhiteSpace(supplierId))
            {
                if (!allSupplierData.TryGetValue(supplierId, out supplierData))
                {
                    // We have a linked supplier via comment line
                    supplierfilter = $"no eq '{supplierId}'";
                }
            }
            else
            {
                if (root.TryGetProperty("vatRegistrationNo", out var vatProp) && vatProp.ValueKind == JsonValueKind.String)
                    vat = vatProp.GetString();

                if (root.TryGetProperty("enterpriseNo", out var enterpriseProp) && enterpriseProp.ValueKind == JsonValueKind.String)
                    enterpriseNumber = enterpriseProp.GetString();

                if (!string.IsNullOrWhiteSpace(enterpriseNumber) && string.IsNullOrWhiteSpace(vat))
                {
                    string escaped = enterpriseNumber.Replace("'", "''");
                    supplierfilter = $"enterpriseNo eq '{escaped}'";
                }
                else if (!string.IsNullOrWhiteSpace(vat) && string.IsNullOrWhiteSpace(enterpriseNumber))
                {
                    string escaped = vat.Replace("'", "''");
                    supplierfilter = $"vatRegistrationNo eq '{escaped}'";
                }
                else if (!string.IsNullOrWhiteSpace(vat) && !string.IsNullOrWhiteSpace(enterpriseNumber))
                {
                    string escaped = vat.Replace("'", "''");
                    supplierfilter = $"vatRegistrationNo eq '{escaped}' or enterpriseNo eq '{enterpriseNumber.Replace("'", "''")}'";
                }
            }

            if (supplierData == null && !string.IsNullOrWhiteSpace(supplierfilter))
            {
                getUrl = supplierUrl + "?$filter=" + Uri.EscapeDataString(supplierfilter);
                var supplierResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

                if (supplierResult != null)
                {
                    allSupplierData[supplierResult["no"]] = supplierResult;
                    supplierData = supplierResult;
                }
            }

            if (supplierData != null)
            {
                if (string.IsNullOrWhiteSpace(supplierfilter))
                {        
                    if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"{supplierId};{company};{number};OK");            
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        ReasonPhrase = $"Supplier found {supplierId}, all good"
                    };
                }
                else
                {
                    if (logger != null) await logger.WarningAsync(EventLog.GetMethodName(), company, $"{supplierId};{company};{number};NOT LINKED"); 
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        ReasonPhrase = $"Supplier found {supplierData["no"]} found, but not linked yet to {company} - {number}, need to link manually in BC"
                    };
                }
            }
            else
            {
                if (logger != null) await logger.WarningAsync(EventLog.GetMethodName(), company, $";{company};{number};NOT FOUND"); 
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    ReasonPhrase = "Supplier not found, need to create new supplier manually"
                };
            }
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, ex);

            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            {
                ReasonPhrase = ex.Message
            };
        }
        finally
        {
            stopwatch.Stop();
            if (logger != null)
            {
                await logger.InfoAsync(EventLog.GetMethodName(), company, $"UpsertSupplierAsync completed in {StringHelper.GetDurationString(stopwatch.Elapsed)}.");
            }
        }
    }
}
