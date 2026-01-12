using System.Text.Json;

namespace SpuntiniBCGateway.Services;

/// <summary>
/// Transforms Peppol vendor credit notes to Business Central purchase credit memos for Bella Sicilia
/// </summary>
public static class BellaSiciliaPurchaseCreditNoteToBcRequest
{
    public static async Task<HttpResponseMessage?> ProcessPeppolCreditNoteAsync(HttpClient client, IConfigurationRoot config, string? company = null, PeppolDocument? peppolDocument = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        ArgumentNullException.ThrowIfNull(peppolDocument);

        string json = ConvertPeppolToPurchaseCreditMemoJson(config, company, peppolDocument, logger);
        
        // Create or update purchase credit memo in BC
        return await PurchaseCreditNoteBCRequest.UpsertPurchaseCreditNoteAsync(client, config, company, json, peppolDocument.Header.Attachment, logger, authHelper, cancellationToken);
    }

    /// <summary>
    /// Converts a Peppol vendor credit note document to a Business Central purchase credit memo JSON representation
    /// </summary>
    public static string ConvertPeppolToPurchaseCreditMemoJson(IConfigurationRoot config, string? company = null, PeppolDocument? peppolDocument = null, EventLog? logger = null)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        ArgumentNullException.ThrowIfNull(peppolDocument);

        string firstDayToProces = config[$"Companies:{company}:PurchaseCreditNoteData:FirstDayToProces"] ?? "UTF8";
        int processHorizonLastXDays = int.TryParse(config[$"Companies:{company}:PurchaseCreditNoteData:ProcessHorizonLastXDays"], out int horizon) ? horizon : 14;

        var header = peppolDocument.Header;

        if (!peppolDocument.ForceUpdate && !string.IsNullOrWhiteSpace(header.IssueDate))
        {
            var docDateDateTime = BellaSiciliaHelper.ParseToDateTime(header.IssueDate);
            if (DateTime.TryParse(firstDayToProces, out var firstDay) && firstDay > docDateDateTime)
            {
                throw new Exception($"Purchase credit note {header.DocumentId} with docdate {header.IssueDate} is before first day to process {firstDayToProces}");
            }

            if (docDateDateTime is not null && docDateDateTime <= DateTime.Now.AddDays(-processHorizonLastXDays))
            {
                throw new Exception($"Purchase credit note {header.DocumentId} with docdate {header.IssueDate} is not within the horizon scope of last {processHorizonLastXDays} days");
            }
        }

        var locationCode = config[$"Companies:{company}:PurchaseCreditNoteData:LocationCodeDefault"] ?? "LALOUVIERE";

        var creditMemo = new Dictionary<string, object>
        {
            // Map header fields
            ["number"] = header.DocumentId,
            ["postingNo"] = header.DocumentId,
            ["documentType"] = "Credit_x0020_Memo",
            ["documentDate"] = header.IssueDate ?? "",
            ["postingDate"] = header.IssueDate ?? "",
            ["currencyCode"] = header.CurrencyCode,
            ["locationCode"] = locationCode,

            // Map vendor (supplier) information
            ["vendorNumber"] = header.SupplierParty.CompanyId ?? header.SupplierParty.VatRegistrationId ?? string.Empty
        };

        // Reference to original invoice if provided
        // if (!string.IsNullOrWhiteSpace(header.RelatedDocumentId))
        // {
        //     creditMemo["appliestoDocNo"] = header.RelatedDocumentId;
        //     creditMemo["appliestoDocType"] = "Invoice";
        // }

        // Map totals (credit notes typically have negative amounts)
        // creditMemo["subTotalExcludingTax"] = -header.TaxExclusiveAmount;
        // creditMemo["totalTaxAmount"] = -header.TaxTotals.Sum(t => t.TaxAmount);
        // creditMemo["totalIncludingTax"] = -header.TaxInclusiveAmount;

        // Map credit memo lines
        var creditLines = new List<object>();
        for (int lineIndex = 0; lineIndex < peppolDocument.DocumentLines.Count; lineIndex++)
        {
            var peppolLine = peppolDocument.DocumentLines[lineIndex];
            var lineObject = new Dictionary<string, object>
            {
             //   { "lineNumber", (lineIndex + 1) * 10000 },
                { "lineType", "Item" },
                { "lineObjectNumber", BellaSiciliaHelper.GetBcItemNumberFromBellaSiciliaItemNumber(config, company, peppolLine.ItemCode ?? string.Empty) ?? string.Empty },
                { "description", peppolLine.ItemName },
                { "quantity", -peppolLine.Quantity }, // Negative for credit notes
                { "unitOfMeasureCode", peppolLine.UnitCode },
                { "lineDiscount", 0},
                { "unitCost", peppolLine.UnitPrice },
                { "documentType", "Credit_x0020_Memo" },
                { "locationCode", locationCode }//,
               // { "lineAmount", -peppolLine.LineExtensionAmount } // Negative for credit notes
            };

            // Map tax information
            if (peppolLine.TaxCategories.Count > 0)
            {
                var taxCategory = peppolLine.TaxCategories.First();
                lineObject["taxCode"] = taxCategory.TaxCategoryId;
            }

            creditLines.Add(lineObject);
        }

        creditMemo["purchaseCreditMemoLines"] = creditLines;

        // Add default BC values from configuration
  //      creditMemo["documentSendingProfile"] = config[$"Companies:{company}:PurchaseCreditMemoData:DocumentSendingProfileDefault"] ?? string.Empty;

        // Serialize to JSON
        return JsonSerializer.Serialize(creditMemo, new JsonSerializerOptions { WriteIndented = false });
    }
}
