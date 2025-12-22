using System.Diagnostics;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static class CustomerBCRequest
{   

    // Attempts to find a customer in Business Central by `number` or `vatRegistrationNumber`.
    // If found, performs a PATCH to update the customer; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage?> UpsertCustomerAsync(HttpClient client, IConfigurationRoot config, string company, string? customerJson, Dictionary<string, Dictionary<string, string>>? allCustomerData = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentException.ThrowIfNullOrEmpty(company);
            if (string.IsNullOrWhiteSpace(customerJson)) throw new ArgumentException("customerJson required", nameof(customerJson));

            string collectionUrl = config[$"Companies:{company}:CustomerData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:CustomerData:DestinationApiUrl required in config");

            // Parse provided JSON to extract number or vatRegistrationNumber for existence check
            using var doc = JsonDocument.Parse(customerJson);
            var root = doc.RootElement;
            string? number = null;
            string? vat = null;
            string? enterpriseNo = null;
            if (root.TryGetProperty("no", out var numProp) && numProp.ValueKind == JsonValueKind.String)
                number = numProp.GetString();
            if (root.TryGetProperty("vatRegistrationNo", out var vatProp) && vatProp.ValueKind == JsonValueKind.String)
                vat = vatProp.GetString();
            if (root.TryGetProperty("enterpriseNo", out var enterpriseProp) && enterpriseProp.ValueKind == JsonValueKind.String)
                enterpriseNo = enterpriseProp.GetString();

            // Build filter expression: prefer number, then vat
            string? filter = null;
            if (!string.IsNullOrWhiteSpace(number))
            {
                string escaped = number.Replace("'", "''");
                filter = $"no eq '{escaped}'";
            }
            else if (!string.IsNullOrWhiteSpace(enterpriseNo))
            {
                string escaped = enterpriseNo.Replace("'", "''");
                filter = $"enterpriseNo eq '{escaped}'";
            }
            else if (!string.IsNullOrWhiteSpace(vat))
            {
                string escaped = vat.Replace("'", "''");
                filter = $"vatRegistrationNo eq '{escaped}'";
            }

            string? existingId = null;
            string? etag = null;
            Dictionary<string, string>? customerResult = [];
            bool isPatchRequired = false;

            if (!string.IsNullOrWhiteSpace(filter))
            {
                if (allCustomerData == null || string.IsNullOrWhiteSpace(number) || !allCustomerData.TryGetValue(number, out customerResult))
                {
                    string getUrl = collectionUrl + "?$filter=" + Uri.EscapeDataString(filter);

                    customerResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;
                }

                if (customerResult != null)
                {
                    customerResult.TryGetValue("systemId", out existingId);
                    customerResult.TryGetValue("@odata.etag", out etag);

                    if (!string.IsNullOrWhiteSpace(existingId))
                    {
                        // Check if PATCH is required by comparing fields
                        isPatchRequired = await JsonHelper.IsPatchRequiredAsync(customerResult, customerJson, ["skipDuplicateCheck"], null, logger, company);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(existingId))
            {
                if (isPatchRequired)
                {
                    // Update: PATCH to customers({id})
                    string updateUrl = $"{collectionUrl}({existingId})";
                    // Remove excluded fields from JSON before PATCH
                    string[]? excludedFields = config.GetSection($"Companies:{company}:CustomerData:FieldsToExcludeFromUpdate").Get<string[]>();

                    customerJson = await JsonHelper.RemoveFieldsFromJsonAsync(customerJson, excludedFields, logger, company);

                    return await BcRequest.PatchBcDataAsync(client, updateUrl, customerJson, etag ?? "*",
                    $"Customer {number} updated successfully.", $"Failed to update customer {number}. Json: {customerJson}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
                }

                stopwatch.Stop();
                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Duration: {StringHelper.GetDurationString(stopwatch.Elapsed)}. Customer {number} already exists. No update required.");
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    ReasonPhrase = $"Customer {number} already exists. No update required."
                };
            }
            else
            {
                return await BcRequest.PostBcDataAsync(client, collectionUrl, customerJson,
                $"Customer {number} created successfully.", $"Failed to create customer {number}. Json: {customerJson}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
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
                await logger.InfoAsync(EventLog.GetMethodName(), company, $"UpsertCustomerAsync completed in {StringHelper.GetDurationString(stopwatch.Elapsed)}.");
            }
        }
    }
}
