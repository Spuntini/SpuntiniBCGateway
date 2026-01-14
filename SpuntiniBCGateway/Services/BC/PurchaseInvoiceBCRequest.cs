using System.Reflection;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static class PurchaseInvoiceBCRequest
{
    // Attempts to find a purchase order in Business Central by 'no' field.
    // If found, performs a PATCH to update the purchase order; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage> UpsertPurchaseInvoiceAsync(HttpClient client, IConfigurationRoot config, string company, string? purchaseInvoiceJson, Attachment? attachment, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentException.ThrowIfNullOrEmpty(company);
            if (string.IsNullOrWhiteSpace(purchaseInvoiceJson)) throw new ArgumentException("purchaseInvoiceJson required", nameof(purchaseInvoiceJson));

            string collectionUrl = config[$"Companies:{company}:PurchaseInvoiceData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:PurchaseInvoiceData:DestinationApiUrl required in config");
           
            // Parse provided JSON to extract 'no' for existence check
            using var doc = JsonDocument.Parse(purchaseInvoiceJson);
            var root = doc.RootElement;
            string? docNum = null;
            if (root.TryGetProperty("number", out var noProp) && noProp.ValueKind == JsonValueKind.String)
                docNum = noProp.GetString();

            string? existingId = null;
            string? etag = null;
            string? date = null;
            string? status = "Draft";
            
            string? documentAttachments = null;
            Dictionary<string, string>? orderResult = null;

            if (string.IsNullOrWhiteSpace(docNum))
                ArgumentNullException.ThrowIfNull(docNum);

            string filter = $"no eq '{docNum.Replace("'", "''")}'";
            var getUrl = collectionUrl + "?$filter=" + Uri.EscapeDataString(filter);

            orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

            if (orderResult != null)
            {
                orderResult.TryGetValue("systemId", out existingId);
                orderResult.TryGetValue("@odata.etag", out etag);
                orderResult.TryGetValue("status", out status);                
                orderResult.TryGetValue("postingDate", out date);                
                orderResult.TryGetValue("documentAttachments", out documentAttachments);                
            }

            if (!string.IsNullOrWhiteSpace(existingId))
            {                
                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Purchase invoice {docNum} - {date} already exists.");
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK);;                
            }
            else
            {
                if (root.TryGetProperty("postingDate", out var postingDate) && postingDate.ValueKind == JsonValueKind.String)
                    date = postingDate.GetString();

                // CREATE SALES INVOICE
                var responseMessage = await BcRequest.PostBcDataAsync(client, collectionUrl + "?$expand=purchaseInvoiceLines", purchaseInvoiceJson,
                $"Purchase invoice {docNum} - {date} created successfully.", $"Failed to create purchase invoice {docNum}. Json: {purchaseInvoiceJson}", EventLog.GetMethodName(), "", logger, company, authHelper, cancellationToken);

                if (!responseMessage.IsSuccessStatusCode)
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK);;

                orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value ?? throw new ArgumentException($"Purchase invoice {docNum} not found after creation");

                orderResult.TryGetValue("systemId", out existingId);
                orderResult.TryGetValue("@odata.etag", out etag);
            }             

            if (orderResult == null)
                throw new ArgumentException($"Purchase invoice {docNum} - {date} not found after creation/update");

            if (attachment != null)
            {                
                List<JsonElement>? attachmentList = JsonHelper.GetItemsSafe(documentAttachments ?? "[]");
                if (attachmentList == null || attachmentList.Count <= 0)
                {
                    var documentAttachmentsData = new Dictionary<string, object>() {
                       { "parentId", existingId ?? ""},
                       { "fileName", attachment.FileName },
                       { "parentType", "Purchase Invoice" }
                    };

                    var documentAttachmentJson = JsonSerializer.Serialize(documentAttachmentsData, new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });

                    var response = await DocumentAttachmentsBCRequest.UpsertDocumentAttachmentsAsync(client, config, company, documentAttachmentJson, attachment, logger, authHelper, cancellationToken);
                    if (response == null || !response.IsSuccessStatusCode)
                        throw new ArgumentException($"Failed to upsert document attachment for purchase invoice {docNum}. Status code: {response?.StatusCode}");

                    orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value ?? throw new ArgumentException($"Purchase invoice {docNum} not found after creation");

                    orderResult.TryGetValue("systemId", out existingId);
                    orderResult.TryGetValue("@odata.etag", out etag);
                }
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);;
            // if (orderResult.TryGetValue("amount", out var amountstr) && double.TryParse(amountstr, out var amount) && Math.Abs(amount) == 0)
            // {
            //     if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Purchase order {docNum} has amount {amount}, no release performed.");
            //     return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            // }   

            // // RELEASE ORDER AND SHIP IF NOT YET RELEASED     
            // return await PurchaseInvoiceActionAsync(client, config, company, "ReleaseAndShipInvoice", existingId, logger, authHelper, cancellationToken);       
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

    // Attempts to find a purchase order in Business Central by 'no' field.
    // If found, performs a PATCH to update the purchase order; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage> PurchaseInvoiceActionAsync(HttpClient client, IConfigurationRoot config, string company, string action, string? purchaseInvoice, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentException.ThrowIfNullOrEmpty(company);
            ArgumentException.ThrowIfNullOrEmpty(action);
            ArgumentException.ThrowIfNullOrEmpty(purchaseInvoice);

            string actionUrl = config[$"Companies:{company}:PurchaseInvoiceData:Actions:{action}"] ?? throw new ArgumentException($"Companies:{company}:PurchaseInvoiceData:Actions:{action}");

            if (!Guid.TryParse(purchaseInvoice, out _))
            {
                string collectionUrl = config[$"Companies:{company}:PurchaseInvoiceData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:PurchaseInvoiceData:DestinationApiUrl required in config");

                string filter = $"number eq '{purchaseInvoice.Replace("'", "''")}'";
                string getUrl = collectionUrl + "?$filter=" + Uri.EscapeDataString(filter);

                var orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "number", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

                if (orderResult != null)
                {
                    _ = orderResult.TryGetValue("id", out purchaseInvoice);
                }
                else
                {
                    throw new ArgumentException($"Purchase invoice {purchaseInvoice} not found");
                }
            }

            if (string.IsNullOrWhiteSpace(purchaseInvoice))
                throw new ArgumentException($"Purchase invoice ID is null or empty");

            actionUrl = actionUrl.Replace("{purchaseInvoiceId}", purchaseInvoice);

            return await BcRequest.PostBcDataAsync(client, actionUrl, "",
                $"Purchase invoice {purchaseInvoice} {action} successfully.", $"Failed to {action} purchase invoice {purchaseInvoice}.", EventLog.GetMethodName(), "", logger, company, authHelper, cancellationToken);
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
