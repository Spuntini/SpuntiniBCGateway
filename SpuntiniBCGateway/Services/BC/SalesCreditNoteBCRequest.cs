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
                if (!"Open".Equals(status, StringComparison.OrdinalIgnoreCase))
                {
                    if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Sales credit note {docNum} - {date} already exist.");
                        return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                }
            }
            else
            {
                if (root.TryGetProperty("postingDate", out var postingDate) && postingDate.ValueKind == JsonValueKind.String)
                    date = postingDate.GetString();

                // CREATE SALES INVOICE
                var responseMessage = await BcRequest.PostBcDataAsync(client, collectionUrl + "?$expand=salesLines", salesCreditNoteJson,
                $"Sales credit note {docNum} - {date} created successfully.", $"Failed to create sales credit note {docNum}. Json: {salesCreditNoteJson}", EventLog.GetMethodName(), "", logger, company, authHelper, cancellationToken);

                if (!responseMessage.IsSuccessStatusCode)
                   throw new ArgumentException($"Failed to create sales credit note {docNum}. Status code: {responseMessage.StatusCode}");

                orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value ?? throw new ArgumentException($"Sales credit note {docNum} not found after creation");

                orderResult.TryGetValue("systemId", out existingId);
                orderResult.TryGetValue("@odata.etag", out etag);
            }

            if (orderResult == null)
                throw new ArgumentException($"Sales credit note {docNum} - {date} not found after creation/update");

            if (attachment != null)
            {
                List<JsonElement>? attachmentList = JsonHelper.GetItemsSafe(documentAttachments ?? "[]");
                if (attachmentList == null || attachmentList.Count <= 0)
                {
                    var documentAttachmentsData = new Dictionary<string, object>() {
                       { "parentId", existingId ?? ""},
                       { "fileName", attachment.FileName },
                       { "parentType", "Sales Credit Memo" }
                    };

                    var documentAttachmentJson = JsonSerializer.Serialize(documentAttachmentsData, new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });

                    var response = await DocumentAttachmentsBCRequest.UpsertDocumentAttachmentsAsync(client, config, company, documentAttachmentJson, attachment, logger, authHelper, cancellationToken);
                    if (response == null || !response.IsSuccessStatusCode)
                        throw new ArgumentException($"Failed to upsert document attachment for sales credit note {docNum}. Status code: {response?.StatusCode}");

                    orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value ?? throw new ArgumentException($"Sales credit note {docNum} not found after creation");

                    orderResult.TryGetValue("systemId", out existingId);
                    orderResult.TryGetValue("@odata.etag", out etag);
                }
            }

            if (orderResult.TryGetValue("amount", out var amountstr) && double.TryParse(amountstr, out var amount) && Math.Abs(amount) == 0)
            {
                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Sales order {docNum} has amount {amount}, no release performed.");
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }   

            // RELEASE ORDER AND SHIP IF NOT YET RELEASED     
            return await SalesCreditNoteActionAsync(client, config, company, "Post", existingId, logger, authHelper, cancellationToken);       
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

            actionUrl = actionUrl.Replace("{salesCreditMemoId}", salesCreditNote);

            return await BcRequest.PostBcDataAsync(client, actionUrl, "",
                $"Sales credit note {salesCreditNote} {action} successfully.", $"Failed to {action} sales credit note {salesCreditNote}.", EventLog.GetMethodName(), "", logger, company, authHelper, cancellationToken);
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
