using System.Text.Json;

namespace SpuntiniBCGateway.Services;

/// <summary>
/// Transforms Peppol vendor invoices to Business Central purchase orders for Bella Sicilia
/// </summary>
public static class BellaSiciliaPurchaseInvoiceToBcRequest
{
    public static async Task<HttpResponseMessage?> ProcessPeppolInvoiceAsync(HttpClient client, IConfigurationRoot config, string? company = null, PeppolDocument? peppolDocument = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        ArgumentNullException.ThrowIfNull(peppolDocument);

        string json = ConvertPeppolToPurchaseInvoiceJson(config, company, peppolDocument, logger);
        
        // Create or update purchase order in BC
        return await PurchaseInvoiceBCRequest.UpsertPurchaseInvoiceAsync(client, config, company, json, peppolDocument.Header.Attachment, logger, authHelper, cancellationToken);        
    }

    /// <summary>
    /// Converts a Peppol vendor invoice document to a Business Central purchase order JSON representation
    /// </summary>
    public static string ConvertPeppolToPurchaseInvoiceJson(IConfigurationRoot config, string? company = null, PeppolDocument? peppolDocument = null, EventLog? logger = null)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        ArgumentNullException.ThrowIfNull(peppolDocument);

        string firstDayToProces = config[$"Companies:{company}:PurchaseInvoiceData:FirstDayToProces"] ?? "UTF8";
        int processHorizonLastXDays = int.TryParse(config[$"Companies:{company}:PurchaseInvoiceData:ProcessHorizonLastXDays"], out int horizon) ? horizon : 14;

        var header = peppolDocument.Header;

        if (!peppolDocument.ForceUpdate && !string.IsNullOrWhiteSpace(header.IssueDate))
        {
            var docDateDateTime = BellaSiciliaHelper.ParseToDateTime(header.IssueDate);
            if (DateTime.TryParse(firstDayToProces, out var firstDay) && firstDay > docDateDateTime)
            {
                throw new Exception($"Purchase invoice {header.DocumentId} with docdate {header.IssueDate} is before first day to process {firstDayToProces}");
            }

            if (docDateDateTime is not null && docDateDateTime <= DateTime.Now.AddDays(-processHorizonLastXDays))
            {
                throw new Exception($"Purchase invoice {header.DocumentId} with docdate {header.IssueDate} is not within the horizon scope of last {processHorizonLastXDays} days");
            }
        }

        var locationCode = config[$"Companies:{company}:PurchaseInvoiceData:LocationCodeDefault"] ?? "LALOUVIERE";

        var document = new Dictionary<string, object>
        {
            // Map header fields
            ["number"] = header.DocumentId,
            ["postingNo"] = header.DocumentId,
            ["documentType"] = "Invoice",
            ["orderDate"] = header.IssueDate ?? "",
            ["dueDate"] = header.DueDate ?? "", // ?? header.IssueDate.AddDays(30),
            ["currencyCode"] = header.CurrencyCode,
            ["buyerReference"] = header.BuyerReference ?? string.Empty,
            ["locationCode"] = locationCode,

            // Map vendor (supplier) information
            ["vendorNumber"] = header.SupplierParty.CompanyId ?? header.SupplierParty.VatRegistrationId ?? string.Empty,
            ["payToVendorNumber"] = header.SupplierParty.CompanyId ?? header.SupplierParty.VatRegistrationId ?? string.Empty,
            ["vendorOrderNo"] = header.OrderReference ?? string.Empty
        };

        // Map delivery information
        if (header.DeliveryInfo?.DeliveryAddress != null)
        {
            var deliveryAddr = header.DeliveryInfo.DeliveryAddress;
            document["shipToName"] =  header.SupplierParty.Name ?? string.Empty;
            document["shipToAddressLine1"] = deliveryAddr.StreetName ?? string.Empty;
            document["shipToCity"] = deliveryAddr.CityName ?? string.Empty;
            document["shipToPostCode"] = deliveryAddr.PostalZone ?? string.Empty;
            document["shipToCountryRegionCode"] = deliveryAddr.CountryCode ?? "BE";
        }

        // Map totals
        // document["subTotalExcludingTax"] = header.TaxExclusiveAmount;
        // document["totalTaxAmount"] = header.TaxTotals.Sum(t => t.TaxAmount);
        // document["totalIncludingTax"] = header.TaxInclusiveAmount;

        // Map purchase lines
        var documentLines = new List<object>();
        for (int lineIndex = 0; lineIndex < peppolDocument.DocumentLines.Count; lineIndex++)
        {
            var peppolLine = peppolDocument.DocumentLines[lineIndex];
            
            var lineObject = new Dictionary<string, object>
            {
               // { "lineNumber", (lineIndex + 1) * 10000 },
                { "lineType", "Item" },
                { "lineObjectNumber", BellaSiciliaHelper.GetBcItemNumberFromBellaSiciliaItemNumber(config, company, peppolLine.ItemCode ?? string.Empty) ?? string.Empty },
                { "description", peppolLine.ItemName },
                { "quantity", peppolLine.Quantity },
                { "lineDiscount", 0},
                { "unitOfMeasureCode", peppolLine.UnitCode },
                { "unitCost", peppolLine.UnitPrice },
                { "documentType", "Invoice" },
                { "locationCode", locationCode }//,
                //{ "lineAmount", peppolLine.LineExtensionAmount }
            };

            // Map tax information
            if (peppolLine.TaxCategories.Count > 0)
            {
                var taxCategory = peppolLine.TaxCategories.First();
                lineObject["taxCode"] = taxCategory.TaxCategoryId;
            }

            documentLines.Add(lineObject);
        }

        document["purchaseInvoiceLines"] = documentLines;

        // Add default BC values from configuration
   //     document["documentSendingProfile"] = config[$"Companies:{company}:PurchaseInvoiceData:DocumentSendingProfileDefault"] ?? string.Empty;
   //     document["paymentTermsCode"] = config[$"Companies:{company}:PurchaseInvoiceData:PaymentTermsCodeDefault"] ?? string.Empty;

        // Serialize to JSON
        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = false });
    }
}
