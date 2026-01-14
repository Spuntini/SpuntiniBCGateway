using System.Xml.Linq;
using System.Globalization;

namespace SpuntiniBCGateway.Services;

/// <summary>
/// Peppol UBL Document file reader that parses XML files and transforms them to PeppolDocument objects
/// Supports invoices, credit notes, and other Peppol document types
/// </summary>
public static class PeppolUblReader
{
    private static readonly XNamespace UblNs = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    private static readonly XNamespace CbcNs = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace CacNs = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";

    /// <summary>
    /// Reads a Peppol UBL XML file and transforms it to a PeppolDocument object
    /// Supports invoices, credit notes, and other document types
    /// </summary>
    public static PeppolDocument ReadPeppolDocument(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Peppol UBL file not found: {filePath}");

        XDocument doc = XDocument.Load(filePath);
        return ParsePeppolDocument(doc);
    }

    /// <summary>
    /// Reads a Peppol UBL XML from a stream and transforms it to a PeppolDocument object
    /// </summary>
    public static PeppolDocument ReadPeppolDocumentFromStream(Stream stream)
    {
        XDocument doc = XDocument.Load(stream);
        return ParsePeppolDocument(doc);
    }

    /// <summary>
    /// Reads a Peppol UBL XML string and transforms it to a PeppolDocument object
    /// </summary>
    public static PeppolDocument ReadPeppolDocumentFromString(string xmlContent)
    {
        XDocument doc = XDocument.Parse(xmlContent);
        return ParsePeppolDocument(doc);
    }

    private static PeppolDocument ParsePeppolDocument(XDocument doc)
    {
        var document = new PeppolDocument();
        var documentElement = doc.Root;

        if (documentElement == null)
            throw new InvalidOperationException("Invalid Peppol UBL XML structure");

        var header = new PeppolDocumentHeader();

        // Parse document header
        header.DocumentId = GetElementValue(documentElement, [CbcNs + "ID"]) ?? string.Empty;
        header.DocumentTypeCode = GetElementValue(documentElement, [CbcNs + "InvoiceTypeCode"]) ?? "380";

        // Determine document type based on type code and element name
        header.DocumentType = DeterminePeppolDocumentType(header.DocumentTypeCode, documentElement.Name.LocalName);

        header.CurrencyCode = GetElementValue(documentElement, [CbcNs + "DocumentCurrencyCode"]) ?? "EUR";

        header.IssueDate = GetElementValue(documentElement, [CbcNs + "IssueDate"]);
        header.DueDate = GetElementValue(documentElement, [CbcNs + "DueDate"]);

        header.BuyerReference = GetElementValue(documentElement, [CbcNs + "BuyerReference"]);
        header.OrderReference = GetElementValue(documentElement, [CacNs + "OrderReference", CbcNs + "ID"]);
        header.DeliveryNoteReference = GetElementValue(documentElement, [CacNs + "DeliveryNoteReference", CbcNs + "ID"]);

        // For credit notes, capture the referenced document
        var billingReferenceElement = documentElement.Element(CacNs + "BillingReference");
        if (billingReferenceElement != null)
        {
            header.RelatedDocumentId = GetElementValue(billingReferenceElement, [CacNs + "InvoiceDocumentReference", CbcNs + "ID"]);
        }

        header.PaymentTermsNote = GetElementValue(documentElement, [CacNs + "PaymentTerms", CbcNs + "Note"]);
        header.PaymentMeansCode = GetElementValue(documentElement, [CacNs + "PaymentMeans", CbcNs + "PaymentMeansCode"]);
        header.PaymentId = GetElementValue(documentElement, [CacNs + "PaymentMeans", CacNs + "PayeeFinancialAccount", CbcNs + "ID"]);

        // Parse supplier party
        var supplierPartyElement = documentElement.Element(CacNs + "AccountingSupplierParty");
        if (supplierPartyElement != null)
        {
            header.SupplierParty = ParseParty(supplierPartyElement);
        }

        // Parse customer party
        var customerPartyElement = documentElement.Element(CacNs + "AccountingCustomerParty");
        if (customerPartyElement != null)
        {
            header.CustomerParty = ParseParty(customerPartyElement);
        }

        // Parse delivery information
        var deliveryElement = documentElement.Element(CacNs + "Delivery");
        if (deliveryElement != null)
        {
            header.DeliveryInfo = ParseDeliveryInfo(deliveryElement);
        }

        // Parse monetary totals
        var monetaryTotalElement = documentElement.Element(CacNs + "LegalMonetaryTotal");
        if (monetaryTotalElement != null)
        {
            header.LineExtensionAmount = ParseDecimal(GetElementValue(monetaryTotalElement, [CbcNs + "LineExtensionTotalAmount"]));
            header.TaxExclusiveAmount = ParseDecimal(GetElementValue(monetaryTotalElement, [CbcNs + "TaxExclusiveAmount"]));
            header.TaxInclusiveAmount = ParseDecimal(GetElementValue(monetaryTotalElement, [CbcNs + "TaxInclusiveAmount"]));
            header.PrepaidAmount = ParseDecimal(GetElementValue(monetaryTotalElement, [CbcNs + "PrepaidAmount"]));
            header.DuePayableAmount = ParseDecimal(GetElementValue(monetaryTotalElement, [CbcNs + "PayableAmount"]));
        }

        // Parse tax totals
        var taxTotalElements = documentElement.Elements(CacNs + "TaxTotal");
        foreach (var taxTotalElement in taxTotalElements)
        {
            header.TaxTotals.Add(ParseTaxTotal(taxTotalElement));
        }

        // Parse additional document reference (PDF attachment)
        var additionalDocRefElement = documentElement.Element(CacNs + "AdditionalDocumentReference");
        if (additionalDocRefElement != null)
        {
            header.Attachment = ParseAttachment(additionalDocRefElement);
        }

        document.Header = header;

        // Parse document lines (invoice or credit note lines)
        var invoiceLineElements = documentElement.Elements(CacNs + "InvoiceLine");
        var creditNoteLineElements = documentElement.Elements(CacNs + "CreditNoteLine");

        foreach (var lineElement in invoiceLineElements)
        {
            document.DocumentLines.Add(ParseDocumentLine(lineElement));
        }

        foreach (var lineElement in creditNoteLineElements)
        {
            document.DocumentLines.Add(ParseDocumentLine(lineElement));
        }

        return document;
    }

    /// <summary>
    /// Determines the Peppol document type based on the type code and element name
    /// </summary>
    private static PeppolDocumentType DeterminePeppolDocumentType(string typeCode, string elementName)
    {
        return (typeCode, elementName) switch
        {
            ("380", "Invoice") => PeppolDocumentType.ClientInvoice,
            ("381", "CreditNote") => PeppolDocumentType.ClientCreditNote,
            ("480", "Invoice") => PeppolDocumentType.VendorInvoice,
            ("481", "CreditNote") => PeppolDocumentType.VendorCreditNote,
            (_, "CreditNote") => PeppolDocumentType.ClientCreditNote,
            _ => PeppolDocumentType.ClientInvoice
        };
    }

    private static PeppolParty ParseParty(XElement partyElement)
    {
        var party = new PeppolParty();

        var partyElement2 = partyElement.Element(CacNs + "Party");
        if (partyElement2 != null)
        {
            party.Name = GetElementValue(partyElement2, [CacNs + "PartyName", CbcNs + "Name"]) ?? string.Empty;
            party.CompanyId = GetElementValue(partyElement2, [CacNs + "PartyIdentification", CbcNs + "ID"]);
            party.VatRegistrationId = GetElementValue(partyElement2, [CacNs + "PartyTaxScheme", CbcNs + "CompanyID"]);
            party.ElectronicMail = GetElementValue(partyElement2, [CacNs + "Contact", CbcNs + "ElectronicMail"]);
            party.RegistrationName = GetElementValue(partyElement2, [CacNs + "PartyLegalEntity", CbcNs + "RegistrationName"]);

            // Parse address
            var addressElement = partyElement2.Element(CacNs + "PostalAddress");
            if (addressElement != null)
            {
                party.PostalAddress = ParseAddress(addressElement);
            }

            // Parse contact
            var contactElement = partyElement2.Element(CacNs + "Contact");
            if (contactElement != null)
            {
                party.Contact = new PeppolContact
                {
                    Name = GetElementValue(contactElement, [CbcNs + "Name"]),
                    Telephone = GetElementValue(contactElement, [CbcNs + "Telephone"]),
                    ElectronicMail = GetElementValue(contactElement, [CbcNs + "ElectronicMail"])
                };
            }
        }

        return party;
    }

    private static PeppolAddress ParseAddress(XElement addressElement)
    {
        return new PeppolAddress
        {
            StreetName = GetElementValue(addressElement, [CbcNs + "StreetName"]),
            BuildingNumber = GetElementValue(addressElement, [CbcNs + "BuildingNumber"]),
            CityName = GetElementValue(addressElement, [CbcNs + "CityName"]),
            PostalZone = GetElementValue(addressElement, [CbcNs + "PostalZone"]),
            CountryCode = GetElementValue(addressElement, [CacNs + "Country", CbcNs + "IdentificationCode"])
        };
    }

    private static PeppolDeliveryInfo ParseDeliveryInfo(XElement deliveryElement)
    {
        var delivery = new PeppolDeliveryInfo
        {
            ActualDeliveryDate = GetElementValue(deliveryElement, [CbcNs + "ActualDeliveryDate"])
        };

        var addressElement = deliveryElement.Element(CacNs + "DeliveryAddress");
        if (addressElement != null)
        {
            delivery.DeliveryAddress = ParseAddress(addressElement);
        }

        return delivery;
    }

    private static PeppolTaxTotal ParseTaxTotal(XElement taxTotalElement)
    {
        var taxTotal = new PeppolTaxTotal
        {
            TaxAmount = ParseDecimal(GetElementValue(taxTotalElement, [CbcNs + "TaxAmount"])),
            CurrencyCode = taxTotalElement.Element(CbcNs + "TaxAmount")?.Attribute("currencyID")?.Value ?? "EUR"
        };

        var taxSubtotalElements = taxTotalElement.Elements(CacNs + "TaxSubtotal");
        foreach (var subtotalElement in taxSubtotalElements)
        {
            taxTotal.TaxSubtotals.Add(ParseTaxSubtotal(subtotalElement));
        }

        return taxTotal;
    }

    private static PeppolTaxSubtotal ParseTaxSubtotal(XElement subtotalElement)
    {
        return new PeppolTaxSubtotal
        {
            TaxableAmount = ParseDecimal(GetElementValue(subtotalElement, [CbcNs + "TaxableAmount"])),
            TaxAmount = ParseDecimal(GetElementValue(subtotalElement, [CbcNs + "TaxAmount"])),
            TaxCategoryId = GetElementValue(subtotalElement, [CacNs + "TaxCategory", CbcNs + "ID"]) ?? string.Empty,
            TaxPercent = ParseDecimal(GetElementValue(subtotalElement, [CacNs + "TaxCategory", CbcNs + "Percent"])),
            TaxSchemeId = GetElementValue(subtotalElement, [CacNs + "TaxCategory", CacNs + "TaxScheme", CbcNs + "ID"]) ?? "VAT"
        };
    }

    private static PeppolDocumentLine ParseDocumentLine(XElement lineElement)
    {
        // Handle both InvoiceLine (InvoicedQuantity) and CreditNoteLine (CreditedQuantity)
        var quantityValue = GetElementValue(lineElement, [CbcNs + "InvoicedQuantity"]) 
            ?? GetElementValue(lineElement, [CbcNs + "CreditedQuantity"]);
        
        var quantityElement = lineElement.Element(CbcNs + "InvoicedQuantity") 
            ?? lineElement.Element(CbcNs + "CreditedQuantity");

        var line = new PeppolDocumentLine
        {
            LineId = GetElementValue(lineElement, [CbcNs + "ID"]) ?? string.Empty,
            Quantity = ParseDecimal(quantityValue),
            UnitCode = quantityElement?.Attribute("unitCode")?.Value ?? string.Empty,
            LineExtensionAmount = ParseDecimal(GetElementValue(lineElement, [CbcNs + "LineExtensionAmount"])),
            AccountingCostCode = GetElementValue(lineElement, [CbcNs + "AccountingCostCode"]) ?? string.Empty
        };

        // Parse item information
        var itemElement = lineElement.Element(CacNs + "Item");
        if (itemElement != null)
        {
            line.ItemName = GetElementValue(itemElement, [CbcNs + "Name"]) ?? string.Empty;
            line.ItemDescription = GetElementValue(itemElement, [CbcNs + "Description"]) ?? string.Empty;
            line.ItemCode = GetElementValue(itemElement, [CacNs + "SellersItemIdentification", CbcNs + "ID"]);
            line.CommodityCode = GetElementValue(itemElement, [CacNs + "CommodityClassification", CbcNs + "ItemClassificationCode"]);

            // Parse tax categories
            var taxCategoryElements = itemElement.Elements(CacNs + "ClassifiedTaxCategory");
            foreach (var taxCatElement in taxCategoryElements)
            {
                line.TaxCategories.Add(new PeppolLineTax
                {
                    TaxCategoryId = GetElementValue(taxCatElement, [CbcNs + "ID"]) ?? string.Empty,
                    TaxPercent = ParseDecimal(GetElementValue(taxCatElement, [CbcNs + "Percent"]))
                });
            }
        }

        // Parse price information
        var priceElement = lineElement.Element(CacNs + "Price");
        if (priceElement != null)
        {
            var priceAmount = ParseDecimal(GetElementValue(priceElement, [CbcNs + "PriceAmount"]));
            // The price amount in Peppol is the line total, so divide by quantity to get unit price
            line.UnitPrice = line.Quantity > 0 ? priceAmount / line.Quantity : priceAmount;
        }

        return line;
    }

    /// <summary>
    /// Helper method to get element value with namespace
    /// </summary>
    private static string? GetElementValue(XElement element, List<XName> elementNameList)
    {
        XElement? currentElement = element;

        foreach (var partElement in elementNameList)
        {
            if (currentElement == null)
                return null;

            // Parse the part to extract namespace and local name
            // Format is either "{namespace}localname" or just "localname"
            XName partName;
            var part = partElement.ToString();

            if (part.StartsWith("{") && part.Contains("}"))
            {
                // Part already has namespace like "{namespace}localname"
                // Extract namespace and local name
                int closeBrace = part.IndexOf('}');
                string ns = part[1..closeBrace];
                string localName = part[(closeBrace + 1)..];
                partName = XName.Get(localName, ns);
            }
            else
            {
                // Part doesn't have namespace, try with element's namespace or without
                partName = XName.Get(part, partElement.NamespaceName);
            }

            currentElement = currentElement.Element(partName);
        }

        return currentElement?.Value;
    }

    /// <summary>
    /// Helper method to safely parse decimal values
    /// </summary>
    private static decimal ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0m;

        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal result))
            return result;

        return 0m;
    }

    /// <summary>
    /// Parses an AdditionalDocumentReference element to extract PDF attachment information
    /// </summary>
    private static Attachment? ParseAttachment(XElement attachmentElement)
    {
        try
        {
            // Parse the embedded document (attachment)
            var embeddedDocElement = attachmentElement.Element(CacNs + "Attachment");
            if (embeddedDocElement != null)
            {
                // Get the embedded binary data
                var embeddedBinaryElement = embeddedDocElement.Element(CbcNs + "EmbeddedDocumentBinaryObject");
                if (embeddedBinaryElement != null)
                {
                    var fileName = GetElementValue(attachmentElement, [CbcNs + "ID"]) ;
                    if (string.IsNullOrWhiteSpace(fileName))
                        fileName = embeddedBinaryElement.Attribute("filename")?.Value;
                    if (string.IsNullOrWhiteSpace(fileName))
                        fileName = "attachment.pdf";

                    var attachment = new Attachment
                    {
                        // Get document ID (filename)
                        FileName = fileName,
                        // Get document type/description
                        DocumentDescription = GetElementValue(attachmentElement, [CbcNs + "DocumentTypeCode"]) ?? "Invoice"
                    };

                    string? encodedContent = embeddedBinaryElement.Value;
                    if (!string.IsNullOrWhiteSpace(encodedContent))
                    {
                        try
                        {
                            // The embedded document is typically base64 encoded
                            attachment.FileContent = Convert.FromBase64String(encodedContent);
                            return attachment;
                        }
                        catch (FormatException)
                        {
                            // If base64 decoding fails, treat as plain bytes
                            attachment.FileContent = System.Text.Encoding.UTF8.GetBytes(encodedContent);
                            return attachment;
                        }
                    }
                }
            }

            return null;
        }
        catch (Exception)
        {
            // If parsing fails, return null - attachment is optional
            return null;
        }
    }
}
