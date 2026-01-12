using System.Net;
using System.Reflection;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static class PurchaseCreditNoteBCRequest
{
    // Attempts to find a purchase order in Business Central by 'no' field.
    // If found, performs a PATCH to update the purchase order; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage> UpsertPurchaseCreditNoteAsync(HttpClient client, IConfigurationRoot config, string company, string? purchaseCreditNoteJson, Attachment? attachment, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentException.ThrowIfNullOrEmpty(company);
            if (string.IsNullOrWhiteSpace(purchaseCreditNoteJson)) throw new ArgumentException("purchaseCreditNoteJson required", nameof(purchaseCreditNoteJson));

            string collectionUrl = config[$"Companies:{company}:PurchaseCreditNoteData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:PurchaseCreditNoteData:DestinationApiUrl required in config");
           
            // Parse provided JSON to extract 'no' for existence check
            using var doc = JsonDocument.Parse(purchaseCreditNoteJson);
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

            string filter = $"number eq '{docNum.Replace("'", "''")}'";
            var getUrl = collectionUrl + "?$filter=" + Uri.EscapeDataString(filter);

            orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "number", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

            if (orderResult != null)
            {
                orderResult.TryGetValue("systemId", out existingId);
                orderResult.TryGetValue("@odata.etag", out etag);
                orderResult.TryGetValue("status", out status);                
                orderResult.TryGetValue("postingDate", out date);                
            }

            if (!string.IsNullOrWhiteSpace(existingId))
            {                
                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Purchase credit note {docNum} - {date} already exist.");
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK);             
            }
            else
            {
                if (root.TryGetProperty("postingDate", out var postingDate) && postingDate.ValueKind == JsonValueKind.String)
                    date = postingDate.GetString();

                // CREATE SALES INVOICE
                var responseMessage = await BcRequest.PostBcDataAsync(client, collectionUrl + "?$expand=purchaseCreditNoteLines", purchaseCreditNoteJson,
                $"Purchase credit note {docNum} - {date} created successfully.", $"Failed to create purchase credit note {docNum}. Json: {purchaseCreditNoteJson}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);

                if (!responseMessage.IsSuccessStatusCode)
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK);

                orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "number", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value ?? throw new ArgumentException($"Purchase credit note {docNum} not found after creation");

                orderResult.TryGetValue("systemId", out existingId);
                orderResult.TryGetValue("@odata.etag", out etag);
            }             

            if (orderResult == null)
                throw new ArgumentException($"Purchase credit note {docNum} - {date} not found after creation/update");

            if (attachment != null)
            {                
                string attachmentUrl = $"{collectionUrl}({existingId})/attachments";
                await BcRequest.AttachFile(client, attachment, attachmentUrl, $"Attachment to purchase credit note {docNum} - {date} added successfully.", $"Failed to add attachment to purchase credit note {docNum}.",
                EventLog.GetMethodName(), logger, company, authHelper, cancellationToken) ;    

                orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value ?? throw new ArgumentException($"Sales invoice {docNum} not found after creation");

                orderResult.TryGetValue("systemId", out existingId);
                orderResult.TryGetValue("@odata.etag", out etag);      
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            // if (orderResult.TryGetValue("amount", out var amountstr) && double.TryParse(amountstr, out var amount) && Math.Abs(amount) == 0)
            // {
            //     if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Purchase order {docNum} has amount {amount}, no release performed.");
            //     return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            // }   

            // // RELEASE ORDER AND SHIP IF NOT YET RELEASED     
            // return await PurchaseCreditNoteActionAsync(client, config, company, "ReleaseAndShipCreditNote", existingId, logger, authHelper, cancellationToken);       
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(MethodBase.GetCurrentMethod()?.Name, company, ex);

            throw;
        }
    }

    // Attempts to find a purchase order in Business Central by 'no' field.
    // If found, performs a PATCH to update the purchase order; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage> PurchaseCreditNoteActionAsync(HttpClient client, IConfigurationRoot config, string company, string action, string? purchaseCreditNote, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentException.ThrowIfNullOrEmpty(company);
            ArgumentException.ThrowIfNullOrEmpty(action);
            ArgumentException.ThrowIfNullOrEmpty(purchaseCreditNote);

            string actionUrl = config[$"Companies:{company}:PurchaseCreditNoteData:Actions:{action}"] ?? throw new ArgumentException($"Companies:{company}:PurchaseCreditNoteData:Actions:{action}");

            if (!Guid.TryParse(purchaseCreditNote, out _))
            {
                string collectionUrl = config[$"Companies:{company}:PurchaseCreditNoteData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:PurchaseCreditNoteData:DestinationApiUrl required in config");

                string filter = $"number eq '{purchaseCreditNote.Replace("'", "''")}'";
                string getUrl = collectionUrl + "?$filter=" + Uri.EscapeDataString(filter);

                var orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "number", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

                if (orderResult != null)
                {
                    _ = orderResult.TryGetValue("id", out purchaseCreditNote);
                }
                else
                {
                    throw new ArgumentException($"Purchase credit note {purchaseCreditNote} not found");
                }
            }

            if (string.IsNullOrWhiteSpace(purchaseCreditNote))
                throw new ArgumentException($"Purchase credit note ID is null or empty");

            actionUrl = actionUrl.Replace("{purchaseCreditNoteId}", purchaseCreditNote);

            return await BcRequest.PostBcDataAsync(client, actionUrl, "",
                $"Purchase credit note {purchaseCreditNote} {action} successfully.", $"Failed to {action} purchase credit note {purchaseCreditNote}.", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(MethodBase.GetCurrentMethod()?.Name, company, ex);

            throw;
        }
    }
}
