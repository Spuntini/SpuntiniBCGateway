using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SpuntiniBCGateway.Services;

public static class DocumentAttachmentsBCRequest
{
    // Attempts to find an item in Business Central by `number`, `no` or `gtin`.
    // If found, performs a PATCH to update the item; otherwise performs a POST to create it.
    public static async Task<HttpResponseMessage?> UpsertDocumentAttachmentsAsync(HttpClient client, IConfigurationRoot config, string company,
    string? json, Attachment attachment, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(company);
            ArgumentNullException.ThrowIfNull(attachment);
            ArgumentNullException.ThrowIfNull(attachment.FileContent);
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Json required", nameof(json));

            string postUrl = config[$"Companies:{company}:DocumentAttachments:PostUrl"] ?? throw new ArgumentException($"Companies:{company}:DocumentAttachments:PostUrl required in config");
            string patchUrl = config[$"Companies:{company}:DocumentAttachments:PatchUrl"] ?? throw new ArgumentException($"Companies:{company}:DocumentAttachments:PatchUrl required in config");

            string? existingId = null;
            int? byteSize = null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? fileName = null;
            string? parentType = null;
            string? parentId = null;

            if (root.TryGetProperty("fileName", out var fileNameProp) && fileNameProp.ValueKind == JsonValueKind.String)
                fileName = fileNameProp.GetString();

            if (root.TryGetProperty("parentType", out var parentTypeProp) && parentTypeProp.ValueKind == JsonValueKind.String)
                parentType = parentTypeProp.GetString();

            if (root.TryGetProperty("parentId", out var parentIdProp) && parentIdProp.ValueKind == JsonValueKind.String)
                parentId = parentIdProp.GetString();

            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(parentType) || string.IsNullOrWhiteSpace(parentId))
                throw new ArgumentException("Not all required fields are available in the JSON");

            // Build filter expression: prefer number, then no, then gtin
            string? filter = null;
            var escaped = fileName.Replace("'", "''");
            var escapedParentType = parentType.Replace("'", "''");
            filter = $"fileName eq '{escaped}' and parentType eq '{escapedParentType}'";

            string getUrl = postUrl + "?$filter=" + filter;

            var result = (await BcRequest.GetBcDataAsync(client, getUrl, "id", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

            if (result != null)
            {
                result.TryGetValue("id", out existingId);
                if (result.TryGetValue("byteSize", out var byteSizeString))
                {
                    if (int.TryParse(byteSizeString, out var parsedByteSize))
                    {
                        byteSize = parsedByteSize;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(existingId) && byteSize.HasValue && byteSize.Value > 0)
            {
                stopwatch.Stop();
                if (logger != null) await logger.InfoAsync(EventLog.GetMethodName(), company, $"Duration: {StringHelper.GetDurationString(stopwatch.Elapsed)}. Document attachment already exists. No update required.");
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    ReasonPhrase = $"Document attachment already exists. No update required."
                };
            }

            HttpResponseMessage postResult;
            if (string.IsNullOrWhiteSpace(existingId))
            {
                postResult = await BcRequest.PostBcDataAsync(client, postUrl, json,
                                $"Document attachment created successfully.", $"Failed to create document attachment. Json: {json}", EventLog.GetMethodName(), "application/json", logger, company, authHelper, cancellationToken);

                if (!postResult.IsSuccessStatusCode)
                    return postResult;

                // Read the JSON from the response body
                string responseContent = await postResult.Content.ReadAsStringAsync();
                using var responseDoc = JsonDocument.Parse(responseContent);
                var postResultJson = responseDoc.RootElement;

                if (postResultJson.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                existingId = idProp.GetString();
            }
            
            if (string.IsNullOrWhiteSpace(existingId))
                throw new ArgumentException("Document attachment ID not found after creation");

            patchUrl = patchUrl.Replace("{attachmentId}", existingId);

            using var request = new HttpRequestMessage(HttpMethod.Patch, patchUrl)
            {
                Content = new ByteArrayContent(attachment.FileContent),
                Headers = { { "If-Match", "*" }, { "Prefer", "return=representation" } }
            };

            var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(attachment!.FileName, out var contentType))
            {
                contentType = "application/octet-stream"; // Default content type
            }

            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            return await BcRequest.SendAsync(client, request, $"Document attachment created successfully.", $"Failed to create document attachment. Json: {json}", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken);
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
                await logger.InfoAsync(EventLog.GetMethodName(), company, $"UpsertItemAsync completed in {StringHelper.GetDurationString(stopwatch.Elapsed)}.");
            }
        }
    }
}
