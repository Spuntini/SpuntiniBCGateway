using System.Reflection;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static class PurchaseOrderBCRequest
{
    // Attempts to find a Purchase order in Business Central by 'no' field.
    // If found, performs a PATCH to update the Purchase order; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage> UpsertPurchaseOrderAsync(HttpClient client, IConfigurationRoot config, string company, string? purchaseOrderJson, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentException.ThrowIfNullOrEmpty(company);
            if (string.IsNullOrWhiteSpace(purchaseOrderJson)) throw new ArgumentException("PurchaseOrderJson required", nameof(purchaseOrderJson));

            string getUrl = config[$"Companies:{company}:PurchaseOrderData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:PurchaseOrderData:DestinationApiUrl required in config");
           
            // Parse provided JSON to extract 'no' for existence check
            using var doc = JsonDocument.Parse(purchaseOrderJson);
            var root = doc.RootElement;
            string? docNum = null;
            if (root.TryGetProperty("no", out var noProp) && noProp.ValueKind == JsonValueKind.String)
                docNum = noProp.GetString();
   
            string? existingId = null;
            string? etag = null;
            string? status = "Open";
            string? date = "";
            Dictionary<string, string>? orderResult = null;

            if (string.IsNullOrWhiteSpace(docNum))
                ArgumentNullException.ThrowIfNull(docNum);

            string filter = $"no eq '{docNum.Replace("'", "''")}'";
            var postAndPatchUrl = getUrl;
            getUrl += "?$filter=" + Uri.EscapeDataString(filter);

            orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

            if (orderResult != null)
            {
                orderResult.TryGetValue("systemId", out existingId);
                orderResult.TryGetValue("@odata.etag", out etag);
                orderResult.TryGetValue("status", out status);           
                orderResult.TryGetValue("orderDate", out date);  
            }

            if (status != null && status.Equals("Released", StringComparison.OrdinalIgnoreCase))
            {
                if (status != null && status.Equals("Released", StringComparison.OrdinalIgnoreCase))
                {
                    if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Purchase order {docNum} - {date}  is already {status}, no update performed.");
                    return new HttpResponseMessage();
                }
            }
            else if (orderResult == null)
            {
                // CREATE Purchase ORDER
                var responseMessage = await BcRequest.PostBcDataAsync(client, postAndPatchUrl + "?$expand=purchaseLines", purchaseOrderJson,
                $"Purchase Order {docNum} - {date} created successfully.", $"Failed to create Purchase order {docNum}. Json: {purchaseOrderJson}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);

                if (!responseMessage.IsSuccessStatusCode)
                    return responseMessage;

                orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value ?? throw new ArgumentException($"Purchase order {docNum} not found after creation");

                orderResult.TryGetValue("systemId", out existingId);
                orderResult.TryGetValue("@odata.etag", out etag);
            }

            if (orderResult == null)
                throw new ArgumentException($"Purchase order {docNum} - {date} not found after creation/update");

            if (orderResult.TryGetValue("amount", out var amountstr) && double.TryParse(amountstr, out var amount) && Math.Abs(amount) == 0)
            {
                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Purchase order {docNum} has amount {amount}, no release performed.");
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }

            // RELEASE ORDER AND SHIP IF NOT YET RELEASED     
            return await PurchaseOrderActionAsync(client, config, company, "ReleaseAndReceive", existingId, logger, authHelper, cancellationToken);
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(MethodBase.GetCurrentMethod()?.Name, company, ex);

            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            {
                ReasonPhrase = ex.Message
            };
        }
    }

    // Attempts to find a Purchase order in Business Central by 'no' field.
    // If found, performs a PATCH to update the Purchase order; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage> PurchaseOrderActionAsync(HttpClient client, IConfigurationRoot config, string company, string action, string? purchaseOrder, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentException.ThrowIfNullOrEmpty(company);
            ArgumentException.ThrowIfNullOrEmpty(action);
            ArgumentException.ThrowIfNullOrEmpty(purchaseOrder);

            string actionUrl = config[$"Companies:{company}:PurchaseOrderData:Actions:{action}"] ?? throw new ArgumentException($"Companies:{company}:PurchaseOrderData:Actions:{action}");

            if (!Guid.TryParse(purchaseOrder, out _))
            {
                string collectionUrl = config[$"Companies:{company}:PurchaseOrderData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:PurchaseOrderData:DestinationApiUrl required in config");

                string filter = $"number eq '{purchaseOrder.Replace("'", "''")}'";
                string getUrl = collectionUrl + "?$filter=" + Uri.EscapeDataString(filter);

                var orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "number", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

                if (orderResult != null)
                {
                    _ = orderResult.TryGetValue("id", out purchaseOrder);
                }
                else
                {
                    throw new ArgumentException($"Purchase order {purchaseOrder} not found");
                }
            }

            if (string.IsNullOrWhiteSpace(purchaseOrder))
                throw new ArgumentException($"Purchase order ID is null or empty");

            actionUrl = actionUrl.Replace("{purchaseOrderId}", purchaseOrder);

            return await BcRequest.PostBcDataAsync(client, actionUrl, "",
                $"Purchase Order {purchaseOrder} {action} successfully.", $"Failed to {action} Purchase order {purchaseOrder}.", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(MethodBase.GetCurrentMethod()?.Name, company, ex);

            throw;
        }
    }
}
