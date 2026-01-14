using System.Text.Json;

namespace SpuntiniBCGateway.Services;

/// <summary>
/// Transforms Peppol client credit notes to Business Central sales credit memos for Bella Sicilia
/// </summary>
public static class BellaSiciliaSalesCreditNoteToBcRequest
{
    public static async Task<HttpResponseMessage?> ProcessPeppolCreditNoteAsync(HttpClient client, IConfigurationRoot config, string? company = null, PeppolDocument? peppolDocument = null, Dictionary<string, Dictionary<string, string>>? allItemData = null,
    Dictionary<string, Dictionary<string, string>>? allCustomerData = null, Dictionary<string, string>? unitOfMeasuresDictionary = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        ArgumentNullException.ThrowIfNull(peppolDocument);

        string json = await ConvertPeppolToSalesCreditMemoJsonAsync(client, config, company, peppolDocument, allItemData, allCustomerData, unitOfMeasuresDictionary, logger, authHelper, cancellationToken);

        // Create or update sales credit memo in BC
        return await SalesCreditNoteBCRequest.UpsertSalesCreditNoteAsync(client, config, company, json, peppolDocument.Header.Attachment, logger, authHelper, cancellationToken);
    }

    /// <summary>
    /// Converts a Peppol client credit note document to a Business Central sales credit memo JSON representation
    /// </summary>
    public static async Task<string> ConvertPeppolToSalesCreditMemoJsonAsync(HttpClient client, IConfigurationRoot config, string? company = null, PeppolDocument? peppolDocument = null, Dictionary<string, Dictionary<string, string>>? allItemData = null,
        Dictionary<string, Dictionary<string, string>>? allCustomerData = null, Dictionary<string, string>? unitOfMeasuresDictionary = null, EventLog? logger = null, AuthHelper? authHelper = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(company) || !company.StartsWith("BELLA", StringComparison.OrdinalIgnoreCase))
            ArgumentException.ThrowIfNullOrEmpty(company, "This method is only for company 'BELLA SICILIA'");

        ArgumentNullException.ThrowIfNull(peppolDocument);
        ArgumentNullException.ThrowIfNull(allCustomerData);
        ArgumentNullException.ThrowIfNull(allItemData);
        ArgumentNullException.ThrowIfNull(unitOfMeasuresDictionary);

        string firstDayToProces = config[$"Companies:{company}:SalesCreditNoteData:FirstDayToProces"] ?? "UTF8";
        int processHorizonLastXDays = int.TryParse(config[$"Companies:{company}:SalesCreditNoteData:ProcessHorizonLastXDays"], out int horizon) ? horizon : 14;

        var header = peppolDocument.Header;

        if (!peppolDocument.ForceUpdate && !string.IsNullOrWhiteSpace(header.IssueDate))
        {
            var docDateDateTime = BellaSiciliaHelper.ParseToDateTime(header.IssueDate);
            if (DateTime.TryParse(firstDayToProces, out var firstDay) && firstDay > docDateDateTime)
            {
                throw new Exception($"Sales credit note {header.DocumentId} with docdate {header.IssueDate} is before first day to process {firstDayToProces}");
            }

            if (docDateDateTime is not null && docDateDateTime <= DateTime.Now.AddDays(-processHorizonLastXDays))
            {
                throw new Exception($"Sales credit note {header.DocumentId} with docdate {header.IssueDate} is not within the horizon scope of last {processHorizonLastXDays} days");
            }
        }

        var countryCode = header.CustomerParty?.PostalAddress?.CountryCode ?? "BE";
        string? customerId;
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

        if (string.IsNullOrWhiteSpace(customerId) || customerId.Length <= 5 || !allCustomerData.TryGetValue(customerId, out Dictionary<string, string>? customerData))
        {
            // Must use customerName.  Possible no VAT number available
            var customerName = header.CustomerParty.Name;

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

        var bcVatBusPostingGroupMapping = BellaSiciliaHelper.GetBcVatBusPostingGroupMapping(config, company);
        customerData.TryGetValue("vatBusPostingGroup", out var vatBusPostingGroup);
        if (string.IsNullOrWhiteSpace(vatBusPostingGroup)) vatBusPostingGroup = config[$"Companies:{company}:CustomerData:VatBusPostingGroupDefault"] ?? "BINNENL";

        var locationCode = config[$"Companies:{company}:SalesCreditNoteData:LocationCodeDefault"] ?? "LALOUVIERE";
        var postingNoSeries = config[$"Companies:{company}:SalesCreditNoteData:PostingNoSeries"] ?? "V-FAC-BESI";
        var creditMemo = new Dictionary<string, object>
        {
            ["documentType"] = "Credit_x0020_Memo",
            // Map header fields
            ["no"] = header.DocumentId,
            ["postingNoSeries"] = postingNoSeries,
            ["postingNo"] = header.DocumentId,
            ["orderDate"] = header.IssueDate ?? "",
            ["postingDate"] = header.IssueDate ?? "",
            ["locationCode"] = locationCode,

            // Map customer (buyer) information
            ["billToCustomerNo"] = customerNo,
            ["sellToCustomerNo"] = customerNo
        };

        // Map credit memo lines
        List<string> unknowItemList = [];

        var creditLines = new List<object>();
        for (int lineIndex = 0; lineIndex < peppolDocument.DocumentLines.Count; lineIndex++)
        {
            var peppolLine = peppolDocument.DocumentLines[lineIndex];

            var itemNo = BellaSiciliaHelper.GetBcItemNumberFromBellaSiciliaItemNumber(config, company, peppolLine.ItemCode ?? string.Empty) ?? string.Empty;

            if (!allItemData.TryGetValue(itemNo, out var itemData))
            {
                try
                {
                    if (!itemNo.StartsWith("A", StringComparison.InvariantCultureIgnoreCase))
                    {
                        await BellaSiciliaItemsExcelToBCRequest.GetItemListAsync(client, config, company, [itemNo], logger, authHelper, cancellationToken);
                    }

                    string escaped = itemNo.Replace("'", "''");
                    var filter = $"no eq '{escaped}'";

                    string collectionUrl = config[$"Companies:{company}:ItemData:DestinationApiUrl"] ?? throw new ArgumentException($"Companies:{company}:ItemData:DestinationApiUrl required in config");

                    string getUrl = collectionUrl + "?$filter=" + filter + "&$expand=itemUnitOfMeasures";
                    itemData = (await BcRequest.GetBcDataAsync(client, getUrl, "no", EventLog.GetMethodName(), logger, company, authHelper, cancellationToken)).FirstOrDefault().Value;

                    allItemData[itemNo] = itemData;
                }
                catch (Exception)
                {
                    if (unknowItemList.Contains(itemNo))
                    {
                        logger?.WarningAsync(EventLog.GetMethodName(), company, $"Item {itemNo} not found in file, but already flagged").Wait();
                        continue;
                    }

                    unknowItemList.Add(itemNo);

                    if (logger != null) await logger.ErrorAsync(EventLog.GetMethodName(), company, new Exception($"Item {itemNo} unknown for document {header.DocumentId}"));
                    break;
                }
            }

            if (!allItemData.TryGetValue(itemNo, out itemData))
            {
                throw new Exception($"Item with id {itemNo} not found");
            }

            if (!unitOfMeasuresDictionary.TryGetValue(peppolLine.UnitCode, out var uom) && string.IsNullOrWhiteSpace(uom))
                itemData.TryGetValue("tradeUnitOfMeasure", out uom);

            var lineObject = new Dictionary<string, object>
            {
                { "type", "Item" },
                { "no", itemNo },
                { "description", peppolLine.ItemName },
                { "quantity", peppolLine.Quantity }, // Negative for credit notes
                { "unitOfMeasureCode", uom ?? "STUKS" },
                { "lineDiscount", 0},
                { "unitPrice", peppolLine.UnitPrice },
                { "documentType", "Credit_x0020_Memo" },
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

            creditLines.Add(lineObject);
        }

        creditMemo["salesLines"] = creditLines;

        // Serialize to JSON
        return JsonSerializer.Serialize(creditMemo, new JsonSerializerOptions { WriteIndented = false });
    }
}
