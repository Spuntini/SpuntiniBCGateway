using System.Reflection;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static class SalesOrderBCRequest
{
    // Attempts to find a sales order in Business Central by 'no' field.
    // If found, performs a PATCH to update the sales order; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage> UpsertSalesOrderAsync(HttpClient client, IConfigurationRoot config, string company, string? salesOrderJson, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentException.ThrowIfNullOrEmpty(company);
            if (string.IsNullOrWhiteSpace(salesOrderJson)) throw new ArgumentException("salesOrderJson required", nameof(salesOrderJson));

            string createUrl = config[$"Companies:{company}:SalesOrderData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:SalesOrderData:DestinationApiUrl required in config");
            string getUrl = config[$"Companies:{company}:SalesOrderData:Actions:Get"] ?? throw new ArgumentException($"Companies:{company}:SalesOrderData:Actions:Get required in config");

            // Parse provided JSON to extract 'no' for existence check
            using var doc = JsonDocument.Parse(salesOrderJson);
            var root = doc.RootElement;
            string? docNum = null;
            if (root.TryGetProperty("number", out var noProp) && noProp.ValueKind == JsonValueKind.String)
                docNum = noProp.GetString();

            string? existingId = null;
            string? etag = null;
            string? date = null;
            string? status = "Draft";
            Dictionary<string, string>? orderResult = null;

            if (string.IsNullOrWhiteSpace(docNum))
                ArgumentNullException.ThrowIfNull(docNum);

            string filter = $"no eq '{docNum.Replace("'", "''")}'";
            getUrl += "?$filter=" + Uri.EscapeDataString(filter);

            orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

            if (orderResult != null)
            {
                orderResult.TryGetValue("systemId", out existingId);
                orderResult.TryGetValue("@odata.etag", out etag);
                orderResult.TryGetValue("status", out status);                
                orderResult.TryGetValue("postingDate", out date);                
            }

            if (!string.IsNullOrWhiteSpace(existingId))
            {                
                if (status != null && status.Equals("Released", StringComparison.OrdinalIgnoreCase))
                {
                    if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Sales order {docNum} - {date} is already {status}, no update performed.");
                    return new HttpResponseMessage();
                }
            }
            else
            {
                if (root.TryGetProperty("postingDate", out var postingDate) && postingDate.ValueKind == JsonValueKind.String)
                    date = postingDate.GetString();

                // CREATE SALES ORDER
                var responseMessage = await BcRequest.PostBcDataAsync(client, createUrl + "?$expand=salesOrderLines", salesOrderJson,
                $"Sales Order {docNum} - {date} created successfully.", $"Failed to create sales order {docNum}. Json: {salesOrderJson}", EventLog.GetMethodName(), "", logger, company, authHelper, cancellationToken);

                if (!responseMessage.IsSuccessStatusCode)
                    return responseMessage;

                orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value ?? throw new ArgumentException($"Sales order {docNum} not found after creation");

                orderResult.TryGetValue("systemId", out existingId);
                orderResult.TryGetValue("@odata.etag", out etag);
            }             

            if (orderResult == null)
                throw new ArgumentException($"Sales order {docNum} - {date} not found after creation/update");

            if (orderResult.TryGetValue("amount", out var amountstr) && double.TryParse(amountstr, out var amount) && Math.Abs(amount) == 0)
            {
                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Sales order {docNum} has amount {amount}, no release performed.");
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }   

            // RELEASE ORDER AND SHIP IF NOT YET RELEASED     
            return await SalesOrderActionAsync(client, config, company, "ReleaseAndShipOrder", existingId, logger, authHelper, cancellationToken);       
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

    // Attempts to find a sales order in Business Central by 'no' field.
    // If found, performs a PATCH to update the sales order; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage> SalesOrderActionAsync(HttpClient client, IConfigurationRoot config, string company, string action, string? salesOrder, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentException.ThrowIfNullOrEmpty(company);
            ArgumentException.ThrowIfNullOrEmpty(action);
            ArgumentException.ThrowIfNullOrEmpty(salesOrder);

            string actionUrl = config[$"Companies:{company}:SalesOrderData:Actions:{action}"] ?? throw new ArgumentException($"Companies:{company}:SalesOrderData:Actions:{action}");

            if (!Guid.TryParse(salesOrder, out _))
            {
                string collectionUrl = config[$"Companies:{company}:SalesOrderData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:SalesOrderData:DestinationApiUrl required in config");

                string filter = $"number eq '{salesOrder.Replace("'", "''")}'";
                string getUrl = collectionUrl + "?$filter=" + Uri.EscapeDataString(filter);

                var orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "number", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

                if (orderResult != null)
                {
                    _ = orderResult.TryGetValue("id", out salesOrder);
                }
                else
                {
                    throw new ArgumentException($"Sales order {salesOrder} not found");
                }
            }

            if (string.IsNullOrWhiteSpace(salesOrder))
                throw new ArgumentException($"Sales order ID is null or empty");

            actionUrl = actionUrl.Replace("{salesOrderId}", salesOrder);

            return await BcRequest.PostBcDataAsync(client, actionUrl, "",
                $"Sales Order {salesOrder} {action} successfully.", $"Failed to {action} sales order {salesOrder}.", EventLog.GetMethodName(), "", logger, company, authHelper, cancellationToken);
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
}
