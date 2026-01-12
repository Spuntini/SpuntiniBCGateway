using System.Text.Json;

namespace SpuntiniBCGateway.Services;

/// <summary>
/// Transforms Peppol client invoices to Business Central sales orders for Bella Sicilia
/// </summary>
public static class BellaSiciliaSalesInvoiceToBcRequest
{
    public static async Task<HttpResponseMessage?> ProcessPeppolInvoiceAsync(HttpClient client, IConfigurationRoot config, string? company = null, PeppolDocument? peppolDocument = null, Dictionary<string, Dictionary<string, string>>? allItemData = null,
    Dictionary<string, Dictionary<string, string>>? allCustomerData = null, Dictionary<string, string>? unitOfMeasuresDictionary = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        ArgumentNullException.ThrowIfNull(peppolDocument);

        string json = await ConvertPeppolToSalesInvoiceJsonAsync(config, company, peppolDocument, allItemData, allCustomerData, unitOfMeasuresDictionary);

        // Create or update sales order in BC
        return await SalesInvoiceBCRequest.UpsertSalesInvoiceAsync(client, config, company, json, peppolDocument.Header.Attachment, logger, authHelper, cancellationToken);
    }

    /// <summary>
    /// Converts a Peppol client invoice document to a Business Central sales order JSON representation
    /// </summary>
    public static async Task<string> ConvertPeppolToSalesInvoiceJsonAsync(IConfigurationRoot config, string? company = null, PeppolDocument? peppolDocument = null, Dictionary<string, Dictionary<string, string>>? allItemData = null,
    Dictionary<string, Dictionary<string, string>>? allCustomerData = null, Dictionary<string, string>? unitOfMeasuresDictionary = null)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        ArgumentNullException.ThrowIfNull(peppolDocument);
        ArgumentNullException.ThrowIfNull(allCustomerData);
        ArgumentNullException.ThrowIfNull(allItemData);
        ArgumentNullException.ThrowIfNull(unitOfMeasuresDictionary);

        string firstDayToProces = config[$"Companies:{company}:SalesInvoiceData:FirstDayToProces"] ?? "UTF8";
        int processHorizonLastXDays = int.TryParse(config[$"Companies:{company}:SalesInvoiceData:ProcessHorizonLastXDays"], out int horizon) ? horizon : 14;

        var header = peppolDocument.Header;

        if (!peppolDocument.ForceUpdate && !string.IsNullOrWhiteSpace(header.IssueDate))
        {
            var docDateDateTime = BellaSiciliaHelper.ParseToDateTime(header.IssueDate);
            if (DateTime.TryParse(firstDayToProces, out var firstDay) && firstDay > docDateDateTime)
            {
                throw new Exception($"Sales invoice {header.DocumentId} with docdate {header.IssueDate} is before first day to process {firstDayToProces}");
            }

            if (docDateDateTime is not null && docDateDateTime <= DateTime.Now.AddDays(-processHorizonLastXDays))
            {
                throw new Exception($"Sales invoice {header.DocumentId} with docdate {header.IssueDate} is not within the horizon scope of last {processHorizonLastXDays} days");
            }
        }

        var countryCode = header.CustomerParty?.PostalAddress?.CountryCode ?? "BE";
        string customerId;
        if (header.CustomerParty != null && !string.IsNullOrWhiteSpace(header.CustomerParty.CompanyId))
        {
            if (header.CustomerParty.CompanyId.StartsWith(countryCode) && countryCode.Equals("BE"))
            {
                customerId = header.CustomerParty.CompanyId.Replace(countryCode, "");
            }
            else
            {
                customerId = header.CustomerParty.CompanyId;
            }
        }
        else if (header.CustomerParty != null && !string.IsNullOrWhiteSpace(header.CustomerParty.VatRegistrationId))
        {
            customerId = header.CustomerParty.VatRegistrationId;
        }
        else
        {
            throw new Exception("Invalid customer id");
        }

        string? customerName;
        if (string.IsNullOrWhiteSpace(customerId) || customerId.Length <= 5 || !allCustomerData.TryGetValue(customerId, out Dictionary<string, string>? customerData))
        {
            // Must use customerName.  Possible no VAT number available
            customerName = header.CustomerParty.Name;

            if (!allCustomerData.TryGetValue(customerName, out customerData))
            {
                foreach (var tempCustomerData in allCustomerData.Values)
                {
                    if (tempCustomerData.TryGetValue("name", out var tempName) && tempName.Equals(customerName, StringComparison.InvariantCultureIgnoreCase))
                    {
                        customerData = tempCustomerData;
                        break;
                    }
                }
            }
        }

        if (customerData == null || !customerData.TryGetValue("no", out var customerNo) || string.IsNullOrWhiteSpace(customerNo))
        {
            throw new Exception($"Customer no not found for customer with id {customerId} not found");
        }

        customerData.TryGetValue("name", out customerName);

        var locationCode = config[$"Companies:{company}:SalesInvoiceData:LocationCodeDefault"] ?? "LALOUVIERE";
        var postingNoSeries = config[$"Companies:{company}:SalesInvoiceData:PostingNoSeries"] ?? "V-FAC-BESI";
        var document = new Dictionary<string, object>
        {
            // Map header fields            
            ["documentType"] = "Invoice",
            ["no"] = header.DocumentId,
            ["postingNoSeries"] = postingNoSeries,
            ["postingNo"] = header.DocumentId,
            ["orderDate"] = header.IssueDate ?? "",
            ["postingDate"] = header.IssueDate ?? "",
            ["dueDate"] = header.DueDate ?? "", // ?? header.IssueDate.AddDays(30),
            ["locationCode"] = locationCode,
            ["externalDocumentNo"] = header.BuyerReference ?? string.Empty,
            // Map customer (buyer) information
            ["billToCustomerNo"] = customerNo,
            ["sellToCustomerNo"] = customerNo
        };

        var bcVatBusPostingGroupMapping = BellaSiciliaHelper.GetBcVatBusPostingGroupMapping(config, company);
        customerData.TryGetValue("vatBusPostingGroup", out var vatBusPostingGroup);
        if (string.IsNullOrWhiteSpace(vatBusPostingGroup)) vatBusPostingGroup = config[$"Companies:{company}:CustomerData:VatBusPostingGroupDefault"] ?? "BINNENL";

        // Map delivery information
        if (header.DeliveryInfo?.DeliveryAddress != null)
        {
            var deliveryAddr = header.DeliveryInfo.DeliveryAddress;
            document["shipToName"] = customerName ?? string.Empty;
            document["shipToAddress"] = deliveryAddr.StreetName ?? string.Empty;
            document["shipToCity"] = deliveryAddr.CityName ?? string.Empty;
            document["shipToPostCode"] = deliveryAddr.PostalZone ?? string.Empty;
            document["shipToCountryRegionCode"] = deliveryAddr.CountryCode ?? "BE";
        }

        // Map sales lines
        var documentLines = new List<object>();
        for (int lineIndex = 0; lineIndex < peppolDocument.DocumentLines.Count; lineIndex++)
        {
            var peppolLine = peppolDocument.DocumentLines[lineIndex];

            Dictionary<string, object> lineObject;

            var itemNo = BellaSiciliaHelper.GetBcItemNumberFromBellaSiciliaItemNumber(config, company, peppolLine.ItemCode ?? string.Empty) ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(itemNo))
            {
                if (!allItemData.TryGetValue(itemNo, out var itemData))
                {
                    throw new Exception($"Item with id {itemNo} not found");
                }

                if (!unitOfMeasuresDictionary.TryGetValue(peppolLine.UnitCode, out var uom) && string.IsNullOrWhiteSpace(uom))
                    itemData.TryGetValue("tradeUnitOfMeasure", out uom);

                lineObject = new Dictionary<string, object>
                {
                    { "type", "Item" },
                    { "no", itemNo },
                    { "description", peppolLine.ItemName },
                    { "quantity", peppolLine.Quantity },
                    { "unitOfMeasureCode", uom ?? "STUKS"},
                    { "lineDiscount", 0},
                    { "unitPrice", peppolLine.UnitPrice },
                    { "documentType", "Invoice" },
                    { "locationCode", locationCode }
                };

                // Map tax information
                if (peppolLine.TaxCategories.Count > 0)
                {
                    var taxCategory = peppolLine.TaxCategories.First();

                    if (bcVatBusPostingGroupMapping.TryGetValue((double)taxCategory.TaxPercent, out string? bcTaxCode) && !string.IsNullOrWhiteSpace(bcTaxCode))
                    {
                        lineObject["vatBusPostingGroup"] = vatBusPostingGroup;
                        lineObject["vatProdPostingGroup"] = bcTaxCode;
                    }
                }
            }
            else
            {
                lineObject = new Dictionary<string, object>
                {
                    { "description", peppolLine.ItemName }
                };
            }

            documentLines.Add(lineObject);
        }

        document["salesLines"] = documentLines;

        // Add default BC values from configuration
    //    document["documentSendingProfile"] = config[$"Companies:{company}:SalesInvoiceData:DocumentSendingProfileDefault"] ?? string.Empty;
    //    document["paymentTermsCode"] = config[$"Companies:{company}:SalesInvoiceData:PaymentTermsCodeDefault"] ?? string.Empty;

        // Serialize to JSON
        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = false });
    }    
}
