using System.Reflection;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static class SalesCreditNoteBCRequest
{
    // Attempts to find a sales order in Business Central by 'no' field.
    // If found, performs a PATCH to update the sales order; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage> UpsertSalesCreditNoteAsync(HttpClient client, IConfigurationRoot config, string company, string? salesCreditNoteJson, Attachment? attachment, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentException.ThrowIfNullOrEmpty(company);
            if (string.IsNullOrWhiteSpace(salesCreditNoteJson)) throw new ArgumentException("salesCreditNoteJson required", nameof(salesCreditNoteJson));

            string collectionUrl = config[$"Companies:{company}:SalesCreditNoteData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:SalesCreditNoteData:DestinationApiUrl required in config");
           
            // Parse provided JSON to extract 'no' for existence check
            using var doc = JsonDocument.Parse(salesCreditNoteJson);
            var root = doc.RootElement;
            string? docNum = null;
            if (root.TryGetProperty("no", out var noProp) && noProp.ValueKind == JsonValueKind.String)
                docNum = noProp.GetString();

            string? existingId = null;
            string? etag = null;
            string? date = null;
            string? status = "Draft";
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
            }

            if (!string.IsNullOrWhiteSpace(existingId))
            {                
                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Sales credit note {docNum} - {date} already exist.");
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK);                
            }
            else
            {
                if (root.TryGetProperty("postingDate", out var postingDate) && postingDate.ValueKind == JsonValueKind.String)
                    date = postingDate.GetString();

                // CREATE SALES INVOICE
                var responseMessage = await BcRequest.PostBcDataAsync(client, collectionUrl + "?$expand=salesLines", salesCreditNoteJson,
                $"Sales credit note {docNum} - {date} created successfully.", $"Failed to create sales credit note {docNum}. Json: {salesCreditNoteJson}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);

                if (!responseMessage.IsSuccessStatusCode)
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK);

                orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value ?? throw new ArgumentException($"Sales credit note {docNum} not found after creation");

                orderResult.TryGetValue("systemId", out existingId);
                orderResult.TryGetValue("@odata.etag", out etag);
            }             

            if (orderResult == null)
                throw new ArgumentException($"Sales credit note {docNum} - {date} not found after creation/update");

            if (attachment != null)
            {                
                string attachmentUrl = $"{collectionUrl}({existingId})/attachments";
                await BcRequest.AttachFile(client, attachment, attachmentUrl, $"Attachment to sales credit note {docNum} - {date} added successfully.", $"Failed to add attachment to sales credit note {docNum}.",
                EventLog.GetMethodName(), logger, company, authHelper, cancellationToken) ;  

                orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value ?? throw new ArgumentException($"Sales invoice {docNum} not found after creation");

                orderResult.TryGetValue("systemId", out existingId);
                orderResult.TryGetValue("@odata.etag", out etag);        
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);;
            // if (orderResult.TryGetValue("amount", out var amountstr) && double.TryParse(amountstr, out var amount) && Math.Abs(amount) == 0)
            // {
            //     if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Sales order {docNum} has amount {amount}, no release performed.");
            //     return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            // }   

            // // RELEASE ORDER AND SHIP IF NOT YET RELEASED     
            // return await SalesCreditNoteActionAsync(client, config, company, "ReleaseAndShipCreditNote", existingId, logger, authHelper, cancellationToken);       
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(MethodBase.GetCurrentMethod()?.Name, company, ex);

            throw;
        }
    }

    // Attempts to find a sales order in Business Central by 'no' field.
    // If found, performs a PATCH to update the sales order; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage> SalesCreditNoteActionAsync(HttpClient client, IConfigurationRoot config, string company, string action, string? salesCreditNote, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentException.ThrowIfNullOrEmpty(company);
            ArgumentException.ThrowIfNullOrEmpty(action);
            ArgumentException.ThrowIfNullOrEmpty(salesCreditNote);

            string actionUrl = config[$"Companies:{company}:SalesCreditNoteData:Actions:{action}"] ?? throw new ArgumentException($"Companies:{company}:SalesCreditNoteData:Actions:{action}");

            if (!Guid.TryParse(salesCreditNote, out _))
            {
                string collectionUrl = config[$"Companies:{company}:SalesCreditNoteData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:SalesCreditNoteData:DestinationApiUrl required in config");

                string filter = $"no eq '{salesCreditNote.Replace("'", "''")}'";
                string getUrl = collectionUrl + "?$filter=" + Uri.EscapeDataString(filter);

                var orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

                if (orderResult != null)
                {
                    _ = orderResult.TryGetValue("systemId", out salesCreditNote);
                }
                else
                {
                    throw new ArgumentException($"Sales credit note {salesCreditNote} not found");
                }
            }

            if (string.IsNullOrWhiteSpace(salesCreditNote))
                throw new ArgumentException($"Sales credit note ID is null or empty");

            actionUrl = actionUrl.Replace("{salesCreditNoteId}", salesCreditNote);

            return await BcRequest.PostBcDataAsync(client, actionUrl, "",
                $"Sales credit note {salesCreditNote} {action} successfully.", $"Failed to {action} sales credit note {salesCreditNote}.", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
        }
        catch (Exception ex)
        {
            if (logger != null) await logger.ErrorAsync(MethodBase.GetCurrentMethod()?.Name, company, ex);

            throw;
        }
    }
}
