using System.Reflection;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static class SalesInvoiceBCRequest
{
    // Attempts to find a sales order in Business Central by 'no' field.
    // If found, performs a PATCH to update the sales order; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage> UpsertSalesInvoiceAsync(HttpClient client, IConfigurationRoot config, string company, string? salesInvoiceJson, Attachment? attachment, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentException.ThrowIfNullOrEmpty(company);
            if (string.IsNullOrWhiteSpace(salesInvoiceJson)) throw new ArgumentException("salesInvoiceJson required", nameof(salesInvoiceJson));

            string collectionUrl = config[$"Companies:{company}:SalesInvoiceData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:SalesInvoiceData:DestinationApiUrl required in config");

            // Parse provided JSON to extract 'no' for existence check
            using var doc = JsonDocument.Parse(salesInvoiceJson);
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
            var getUrl = collectionUrl + "?$filter=" + Uri.EscapeDataString(filter) + "&$expand=documentAttachments";

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
                    if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Sales invoice {docNum} - {date} already exists.");
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                }
            }
            else
            {
                if (root.TryGetProperty("postingDate", out var postingDate) && postingDate.ValueKind == JsonValueKind.String)
                    date = postingDate.GetString();

                // CREATE SALES INVOICE
                var responseMessage = await BcRequest.PostBcDataAsync(client, collectionUrl + "?$expand=salesLines", salesInvoiceJson,
                $"Sales invoice {docNum} - {date} created successfully.", $"Failed to create sales invoice {docNum}. Json: {salesInvoiceJson}", EventLog.GetMethodName(), "", logger, company, authHelper, cancellationToken);

                if (!responseMessage.IsSuccessStatusCode)
                    throw new ArgumentException($"Failed to create sales invoice {docNum}. Status code: {responseMessage.StatusCode}");

                orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value ?? throw new ArgumentException($"Sales invoice {docNum} not found after creation");

                orderResult.TryGetValue("systemId", out existingId);
                orderResult.TryGetValue("@odata.etag", out etag);
            }

            if (orderResult == null)
                throw new ArgumentException($"Sales invoice {docNum} - {date} not found after creation/update");

            if (attachment != null)
            {
                List<JsonElement>? attachmentList = JsonHelper.GetItemsSafe(documentAttachments ?? "[]");
                if (attachmentList == null || attachmentList.Count <= 0)
                {
                    var documentAttachmentsData = new Dictionary<string, object>() {
                       { "parentId", existingId ?? ""},
                       { "fileName", attachment.FileName },
                       { "parentType", "Sales Invoice" }
                    };

                    var documentAttachmentJson = JsonSerializer.Serialize(documentAttachmentsData, new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });

                    var response = await DocumentAttachmentsBCRequest.UpsertDocumentAttachmentsAsync(client, config, company, documentAttachmentJson, attachment, logger, authHelper, cancellationToken);
                    if (response == null || !response.IsSuccessStatusCode)
                        throw new ArgumentException($"Failed to upsert document attachment for sales invoice {docNum}. Status code: {response?.StatusCode}");

                    orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value ?? throw new ArgumentException($"Sales invoice {docNum} not found after creation");

                    orderResult.TryGetValue("systemId", out existingId);
                    orderResult.TryGetValue("@odata.etag", out etag);
                }
            }

            if (orderResult.TryGetValue("amount", out var amountstr) && double.TryParse(amountstr, out var amount) && Math.Abs(amount) == 0)
            {
                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Sales invoice {docNum} has amount {amount}, no post performed.");
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }

            // RELEASE ORDER AND SHIP IF NOT YET RELEASED     
            return await SalesInvoiceActionAsync(client, config, company, "Post", existingId, logger, authHelper, cancellationToken);
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
    public static async Task<HttpResponseMessage> SalesInvoiceActionAsync(HttpClient client, IConfigurationRoot config, string company, string action, string? salesInvoice, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentException.ThrowIfNullOrEmpty(company);
            ArgumentException.ThrowIfNullOrEmpty(action);
            ArgumentException.ThrowIfNullOrEmpty(salesInvoice);

            string actionUrl = config[$"Companies:{company}:SalesInvoiceData:Actions:{action}"] ?? throw new ArgumentException($"Companies:{company}:SalesInvoiceData:Actions:{action}");

            if (!Guid.TryParse(salesInvoice, out _))
            {
                string collectionUrl = config[$"Companies:{company}:SalesInvoiceData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:SalesInvoiceData:DestinationApiUrl required in config");

                string filter = $"no eq '{salesInvoice.Replace("'", "''")}'";
                string getUrl = collectionUrl + "?$filter=" + Uri.EscapeDataString(filter);

                var orderResult = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

                if (orderResult != null)
                {
                    _ = orderResult.TryGetValue("systemId", out salesInvoice);
                }
                else
                {
                    throw new ArgumentException($"Sales invoice {salesInvoice} not found");
                }
            }

            if (string.IsNullOrWhiteSpace(salesInvoice))
                throw new ArgumentException($"Sales invoice ID is null or empty");

            actionUrl = actionUrl.Replace("{salesInvoiceId}", salesInvoice);

            var response = await BcRequest.PostBcDataAsync(client, actionUrl, "",
                $"Sales invoice {salesInvoice} {action} successfully.", $"Failed to {action} sales invoice {salesInvoice}.", EventLog.GetMethodName(), "", logger, company, authHelper, cancellationToken);

            if (response == null || !response.IsSuccessStatusCode)
                throw new ArgumentException($"Failed to {action} sales invoice {salesInvoice}. Status code: {response?.StatusCode}");

            return response;
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
