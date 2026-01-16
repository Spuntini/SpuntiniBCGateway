using System.Xml.Linq;
using System.Globalization;

namespace SpuntiniBCGateway.Services;

/// <summary>
/// Peppol UBL Document writer that transforms PeppolDocument objects to valid Peppol UBL XML files.
/// Supports invoices, credit notes, and other Peppol document types with all required and optional fields.
/// </summary>
public static class PeppolUblWriter
{
    private static readonly XNamespace UblNs = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    private static readonly XNamespace CbcNs = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace CacNs = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace XsiNs = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>
    /// Writes a PeppolDocument to a file path, creating a valid Peppol UBL XML document
    /// </summary>
    public static void WritePeppolDocument(PeppolDocument document, string filePath)
    {
        var xmlDoc = CreatePeppolDocument(document);
        xmlDoc.Save(filePath);
    }

    /// <summary>
    /// Writes a PeppolDocument to a stream, creating a valid Peppol UBL XML document
    /// </summary>
    public static void WritePeppolDocumentToStream(PeppolDocument document, Stream stream)
    {
        var xmlDoc = CreatePeppolDocument(document);
        xmlDoc.Save(stream);
    }

    /// <summary>
    /// Converts a PeppolDocument to an XML string representing a valid Peppol UBL document
    /// </summary>
    public static string WritePeppolDocumentToString(PeppolDocument document)
    {
        var xmlDoc = CreatePeppolDocument(document);
        return xmlDoc.ToString();
    }

    /// <summary>
    /// Creates the root XML document from a PeppolDocument
    /// </summary>
    private static XDocument CreatePeppolDocument(PeppolDocument document)
    {
        if (document?.Header == null)
            throw new ArgumentNullException(nameof(document), "Document and Header cannot be null");

        var elementName = GetElementNameForDocumentType(document.Header.DocumentType);
        var root = new XElement(UblNs + elementName);
        root.SetAttributeValue(XNamespace.Xmlns + "cbc", "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2");
        root.SetAttributeValue(XNamespace.Xmlns + "cac", "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2");
        root.SetAttributeValue(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance");
        root.Add(new XAttribute("xmlns", "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"));

        // Add header elements
        AddDocumentHeader(root, document.Header);

        // Add lines
        AddDocumentLines(root, document.Header.DocumentType, document.DocumentLines);

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    /// <summary>
    /// Gets the element name (Invoice or CreditNote) based on document type
    /// </summary>
    private static string GetElementNameForDocumentType(PeppolDocumentType documentType)
    {
        return documentType switch
        {
            PeppolDocumentType.ClientInvoice => "Invoice",
            PeppolDocumentType.ClientCreditNote => "CreditNote",
            PeppolDocumentType.VendorInvoice => "Invoice",
            PeppolDocumentType.VendorCreditNote => "CreditNote",
            _ => "Invoice"
        };
    }

    /// <summary>
    /// Gets the type code for the document type
    /// </summary>
    private static string GetTypeCodeForDocumentType(PeppolDocumentType documentType)
    {
        return documentType switch
        {
            PeppolDocumentType.ClientInvoice => "380",
            PeppolDocumentType.ClientCreditNote => "381",
            PeppolDocumentType.VendorInvoice => "480",
            PeppolDocumentType.VendorCreditNote => "481",
            _ => "380"
        };
    }

    /// <summary>
    /// Adds the document header (metadata and party information) to the XML root
    /// </summary>
    private static void AddDocumentHeader(XElement root, PeppolDocumentHeader header)
    {
        // Document identification
        AddElement(root, CbcNs + "ID", header.DocumentId ?? string.Empty);
        AddElement(root, CbcNs + "IssueDate", header.IssueDate ?? DateTime.Now.ToString("yyyy-MM-dd"));

        // Document type code
        var typeCode = GetTypeCodeForDocumentType(header.DocumentType);
        AddElement(root, CbcNs + "InvoiceTypeCode", typeCode);

        // Optional: due date
        if (!string.IsNullOrEmpty(header.DueDate))
            AddElement(root, CbcNs + "DueDate", header.DueDate);

        // Currency code
        AddElement(root, CbcNs + "DocumentCurrencyCode", header.CurrencyCode ?? "EUR");

        // Optional: buyer reference
        if (!string.IsNullOrEmpty(header.BuyerReference))
            AddElement(root, CbcNs + "BuyerReference", header.BuyerReference);

        // Optional: order reference
        if (!string.IsNullOrEmpty(header.OrderReference))
        {
            var orderRefElement = new XElement(CacNs + "OrderReference",
                new XElement(CbcNs + "ID", header.OrderReference)
            );
            root.Add(orderRefElement);
        }

        // Optional: delivery note reference
        if (!string.IsNullOrEmpty(header.DeliveryNoteReference))
        {
            var deliveryRefElement = new XElement(CacNs + "DeliveryNoteReference",
                new XElement(CbcNs + "ID", header.DeliveryNoteReference)
            );
            root.Add(deliveryRefElement);
        }

        // For credit notes: add billing reference (reference to original invoice)
        if ((header.DocumentType == PeppolDocumentType.ClientCreditNote || 
             header.DocumentType == PeppolDocumentType.VendorCreditNote) && 
            !string.IsNullOrEmpty(header.RelatedDocumentId))
        {
            var billingRefElement = new XElement(CacNs + "BillingReference",
                new XElement(CacNs + "InvoiceDocumentReference",
                    new XElement(CbcNs + "ID", header.RelatedDocumentId)
                )
            );
            root.Add(billingRefElement);
        }

        // Add supplier party
        if (header.SupplierParty != null)
        {
            var supplierElement = new XElement(CacNs + "AccountingSupplierParty");
            AddPartyInfo(supplierElement, header.SupplierParty);
            root.Add(supplierElement);
        }

        // Add customer party
        if (header.CustomerParty != null)
        {
            var customerElement = new XElement(CacNs + "AccountingCustomerParty");
            AddPartyInfo(customerElement, header.CustomerParty);
            root.Add(customerElement);
        }

        // Optional: delivery information
        if (header.DeliveryInfo != null)
        {
            var deliveryElement = CreateDeliveryElement(header.DeliveryInfo);
            root.Add(deliveryElement);
        }

        // Optional: payment terms
        if (!string.IsNullOrEmpty(header.PaymentTermsNote))
        {
            var paymentTermsElement = new XElement(CacNs + "PaymentTerms",
                new XElement(CbcNs + "Note", header.PaymentTermsNote)
            );
            root.Add(paymentTermsElement);
        }

        // Optional: payment means
        if (!string.IsNullOrEmpty(header.PaymentMeansCode) || !string.IsNullOrEmpty(header.PaymentId))
        {
            var paymentMeansElement = new XElement(CacNs + "PaymentMeans");

            if (!string.IsNullOrEmpty(header.PaymentMeansCode))
                paymentMeansElement.Add(new XElement(CbcNs + "PaymentMeansCode", header.PaymentMeansCode));

            if (!string.IsNullOrEmpty(header.PaymentId))
            {
                paymentMeansElement.Add(new XElement(CacNs + "PayeeFinancialAccount",
                    new XElement(CbcNs + "ID", header.PaymentId)
                ));
            }

            root.Add(paymentMeansElement);
        }

        // Add tax totals
        var headerCurrencyCode = header.CurrencyCode ?? "EUR";
        foreach (var taxTotal in header.TaxTotals)
        {
            var taxTotalElement = CreateTaxTotalElement(taxTotal, headerCurrencyCode);
            root.Add(taxTotalElement);
        }

        // Add monetary totals
        var monetaryTotalElement = new XElement(CacNs + "LegalMonetaryTotal");
        AddElement(monetaryTotalElement, CbcNs + "LineExtensionTotalAmount", 
            FormatDecimal(header.LineExtensionAmount), headerCurrencyCode);
        AddElement(monetaryTotalElement, CbcNs + "TaxExclusiveAmount", 
            FormatDecimal(header.TaxExclusiveAmount), headerCurrencyCode);
        AddElement(monetaryTotalElement, CbcNs + "TaxInclusiveAmount", 
            FormatDecimal(header.TaxInclusiveAmount), headerCurrencyCode);

        if (header.PrepaidAmount > 0)
            AddElement(monetaryTotalElement, CbcNs + "PrepaidAmount", 
                FormatDecimal(header.PrepaidAmount), headerCurrencyCode);

        AddElement(monetaryTotalElement, CbcNs + "PayableAmount", 
            FormatDecimal(header.DuePayableAmount), headerCurrencyCode);

        root.Add(monetaryTotalElement);

        // Optional: add attachment (PDF)
        if (header.Attachment != null)
        {
            var attachmentElement = CreateAttachmentElement(header.Attachment);
            root.Add(attachmentElement);
        }
    }

    /// <summary>
    /// Adds party information (supplier or customer) to the parent element
    /// </summary>
    private static void AddPartyInfo(XElement parentElement, PeppolParty party)
    {
        var partyElement = new XElement(CacNs + "Party");

        // Party identification
        if (!string.IsNullOrEmpty(party.CompanyId))
        {
            partyElement.Add(new XElement(CacNs + "PartyIdentification",
                new XElement(CbcNs + "ID", party.CompanyId)
            ));
        }

        // Party name
        if (!string.IsNullOrEmpty(party.Name))
        {
            partyElement.Add(new XElement(CacNs + "PartyName",
                new XElement(CbcNs + "Name", party.Name)
            ));
        }

        // Postal address
        if (party.PostalAddress != null)
        {
            var addressElement = CreateAddressElement(party.PostalAddress);
            partyElement.Add(addressElement);
        }

        // Party tax scheme (VAT)
        if (!string.IsNullOrEmpty(party.VatRegistrationId))
        {
            partyElement.Add(new XElement(CacNs + "PartyTaxScheme",
                new XElement(CbcNs + "CompanyID", party.VatRegistrationId),
                new XElement(CacNs + "TaxScheme",
                    new XElement(CbcNs + "ID", "VAT")
                )
            ));
        }

        // Party legal entity
        if (!string.IsNullOrEmpty(party.RegistrationName) || !string.IsNullOrEmpty(party.Name))
        {
            partyElement.Add(new XElement(CacNs + "PartyLegalEntity",
                new XElement(CbcNs + "RegistrationName", party.RegistrationName ?? party.Name)
            ));
        }

        // Contact information
        if (party.Contact != null || !string.IsNullOrEmpty(party.ElectronicMail))
        {
            var contactElement = new XElement(CacNs + "Contact");

            if (party.Contact != null)
            {
                if (!string.IsNullOrEmpty(party.Contact.Name))
                    contactElement.Add(new XElement(CbcNs + "Name", party.Contact.Name));

                if (!string.IsNullOrEmpty(party.Contact.Telephone))
                    contactElement.Add(new XElement(CbcNs + "Telephone", party.Contact.Telephone));

                if (!string.IsNullOrEmpty(party.Contact.ElectronicMail))
                    contactElement.Add(new XElement(CbcNs + "ElectronicMail", party.Contact.ElectronicMail));
            }
            else if (!string.IsNullOrEmpty(party.ElectronicMail))
            {
                contactElement.Add(new XElement(CbcNs + "ElectronicMail", party.ElectronicMail));
            }

            if (contactElement.HasElements)
                partyElement.Add(contactElement);
        }

        parentElement.Add(partyElement);
    }

    /// <summary>
    /// Creates an address element from PeppolAddress
    /// </summary>
    private static XElement CreateAddressElement(PeppolAddress address)
    {
        var addressElement = new XElement(CacNs + "PostalAddress");

        if (!string.IsNullOrEmpty(address.StreetName))
            addressElement.Add(new XElement(CbcNs + "StreetName", address.StreetName));

        if (!string.IsNullOrEmpty(address.BuildingNumber))
            addressElement.Add(new XElement(CbcNs + "BuildingNumber", address.BuildingNumber));

        if (!string.IsNullOrEmpty(address.CityName))
            addressElement.Add(new XElement(CbcNs + "CityName", address.CityName));

        if (!string.IsNullOrEmpty(address.PostalZone))
            addressElement.Add(new XElement(CbcNs + "PostalZone", address.PostalZone));

        if (!string.IsNullOrEmpty(address.CountryCode))
        {
            addressElement.Add(new XElement(CacNs + "Country",
                new XElement(CbcNs + "IdentificationCode", address.CountryCode)
            ));
        }

        return addressElement;
    }

    /// <summary>
    /// Creates a delivery element from PeppolDeliveryInfo
    /// </summary>
    private static XElement CreateDeliveryElement(PeppolDeliveryInfo deliveryInfo)
    {
        var deliveryElement = new XElement(CacNs + "Delivery");

        if (!string.IsNullOrEmpty(deliveryInfo.ActualDeliveryDate))
            deliveryElement.Add(new XElement(CbcNs + "ActualDeliveryDate", deliveryInfo.ActualDeliveryDate));

        if (deliveryInfo.DeliveryAddress != null)
        {
            var addressElement = CreateAddressElement(deliveryInfo.DeliveryAddress);
            var deliveryAddressElement = new XElement(CacNs + "DeliveryAddress");
            deliveryAddressElement.Add(addressElement.Elements());
            deliveryElement.Add(deliveryAddressElement);
        }

        return deliveryElement;
    }

    /// <summary>
    /// Creates a tax total element from PeppolTaxTotal
    /// </summary>
    private static XElement CreateTaxTotalElement(PeppolTaxTotal taxTotal, string currencyCode)
    {
        var taxTotalElement = new XElement(CacNs + "TaxTotal");

        AddElement(taxTotalElement, CbcNs + "TaxAmount", 
            FormatDecimal(taxTotal.TaxAmount), taxTotal.CurrencyCode ?? currencyCode);

        foreach (var subtotal in taxTotal.TaxSubtotals)
        {
            var subtotalElement = new XElement(CacNs + "TaxSubtotal");

            AddElement(subtotalElement, CbcNs + "TaxableAmount", 
                FormatDecimal(subtotal.TaxableAmount), currencyCode);
            AddElement(subtotalElement, CbcNs + "TaxAmount", 
                FormatDecimal(subtotal.TaxAmount), currencyCode);

            var taxCategoryElement = new XElement(CacNs + "TaxCategory");
            AddElement(taxCategoryElement, CbcNs + "ID", subtotal.TaxCategoryId);

            if (subtotal.TaxPercent > 0)
                AddElement(taxCategoryElement, CbcNs + "Percent", FormatDecimal(subtotal.TaxPercent));

            taxCategoryElement.Add(new XElement(CacNs + "TaxScheme",
                new XElement(CbcNs + "ID", subtotal.TaxSchemeId)
            ));

            subtotalElement.Add(taxCategoryElement);
            taxTotalElement.Add(subtotalElement);
        }

        return taxTotalElement;
    }

    /// <summary>
    /// Creates an attachment element from Attachment object
    /// </summary>
    private static XElement CreateAttachmentElement(Attachment attachment)
    {
        var additionalDocRefElement = new XElement(CacNs + "AdditionalDocumentReference");

        // Document ID (filename)
        AddElement(additionalDocRefElement, CbcNs + "ID", attachment.FileName ?? "attachment.pdf");

        // Document type code
        AddElement(additionalDocRefElement, CbcNs + "DocumentTypeCode", 
            attachment.DocumentDescription ?? "Invoice");

        // Embedded document
        if (attachment.FileContent != null && attachment.FileContent.Length > 0)
        {
            var attachmentSubElement = new XElement(CacNs + "Attachment");

            var encodedContent = Convert.ToBase64String(attachment.FileContent);
            var embeddedBinaryElement = new XElement(CbcNs + "EmbeddedDocumentBinaryObject",
                new XAttribute("filename", attachment.FileName ?? "attachment.pdf"),
                new XAttribute("mimeCode", "application/pdf"),
                encodedContent
            );

            attachmentSubElement.Add(embeddedBinaryElement);
            additionalDocRefElement.Add(attachmentSubElement);
        }

        return additionalDocRefElement;
    }

    /// <summary>
    /// Adds document lines (invoice lines or credit note lines) to the root element
    /// </summary>
    private static void AddDocumentLines(XElement root, PeppolDocumentType documentType, List<PeppolDocumentLine> lines)
    {
        var lineElementName = (documentType == PeppolDocumentType.ClientCreditNote || 
                               documentType == PeppolDocumentType.VendorCreditNote) ? "CreditNoteLine" : "InvoiceLine";
        var quantityElementName = (documentType == PeppolDocumentType.ClientCreditNote || 
                                   documentType == PeppolDocumentType.VendorCreditNote) ? "CreditedQuantity" : "InvoicedQuantity";

        foreach (var line in lines)
        {
            var lineElement = new XElement(CacNs + lineElementName);

            // Line identification
            AddElement(lineElement, CbcNs + "ID", line.LineId ?? string.Empty);

            // Quantity with unit code
            var quantityElement = new XElement(CbcNs + quantityElementName, FormatDecimal(line.Quantity));
            if (!string.IsNullOrEmpty(line.UnitCode))
                quantityElement.SetAttributeValue("unitCode", line.UnitCode);
            lineElement.Add(quantityElement);

            // Optional: line extension amount
            AddElement(lineElement, CbcNs + "LineExtensionAmount", 
                FormatDecimal(line.LineExtensionAmount), "EUR");

            // Optional: accounting cost code
            if (!string.IsNullOrEmpty(line.AccountingCostCode))
                AddElement(lineElement, CbcNs + "AccountingCostCode", line.AccountingCostCode);

            // Item information
            var itemElement = new XElement(CacNs + "Item");

            if (!string.IsNullOrEmpty(line.ItemName))
                AddElement(itemElement, CbcNs + "Name", line.ItemName);

            if (!string.IsNullOrEmpty(line.ItemDescription))
                AddElement(itemElement, CbcNs + "Description", line.ItemDescription);

            // Item identification (Seller's item ID)
            if (!string.IsNullOrEmpty(line.ItemCode))
            {
                itemElement.Add(new XElement(CacNs + "SellersItemIdentification",
                    new XElement(CbcNs + "ID", line.ItemCode)
                ));
            }

            // Commodity classification
            if (!string.IsNullOrEmpty(line.CommodityCode))
            {
                itemElement.Add(new XElement(CacNs + "CommodityClassification",
                    new XElement(CbcNs + "ItemClassificationCode", line.CommodityCode)
                ));
            }

            // Tax categories for the item
            foreach (var taxCategory in line.TaxCategories)
            {
                var taxCatElement = new XElement(CacNs + "ClassifiedTaxCategory");
                AddElement(taxCatElement, CbcNs + "ID", taxCategory.TaxCategoryId);

                if (taxCategory.TaxPercent > 0)
                    AddElement(taxCatElement, CbcNs + "Percent", FormatDecimal(taxCategory.TaxPercent));

                taxCatElement.Add(new XElement(CacNs + "TaxScheme",
                    new XElement(CbcNs + "ID", taxCategory.TaxSchemeId)
                ));

                itemElement.Add(taxCatElement);
            }

            lineElement.Add(itemElement);

            // Price information
            if (line.UnitPrice > 0)
            {
                var priceElement = new XElement(CacNs + "Price");
                // In Peppol, PriceAmount is typically the line total, not unit price
                // But we store unit price in the model, so we calculate line total
                var priceAmount = line.UnitPrice * line.Quantity;
                AddElement(priceElement, CbcNs + "PriceAmount", 
                    FormatDecimal(priceAmount), "EUR");
                lineElement.Add(priceElement);
            }

            root.Add(lineElement);
        }
    }

    /// <summary>
    /// Adds an element with text content to a parent element
    /// </summary>
    private static void AddElement(XElement parent, XName name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            parent.Add(new XElement(name, value));
    }

    /// <summary>
    /// Adds an element with text content and a currency attribute to a parent element
    /// </summary>
    private static void AddElement(XElement parent, XName name, string value, string currencyCode)
    {
        var element = new XElement(name, value);
        if (!string.IsNullOrEmpty(currencyCode))
            element.SetAttributeValue("currencyID", currencyCode);
        parent.Add(element);
    }

    /// <summary>
    /// Formats a decimal value for XML output using invariant culture
    /// </summary>
    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Represents an attachment (e.g., PDF file) to be embedded in a Peppol document
/// </summary>
public class Attachment
{
    public string FileName { get; set; } = string.Empty;
    public string DocumentDescription { get; set; } = "Invoice";
    public byte[]? FileContent { get; set; }

    /// <summary>
    /// Creates an attachment from a file path
    /// </summary>
    public static Attachment FromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        var fileName = Path.GetFileName(filePath);
        var fileContent = File.ReadAllBytes(filePath);

        return new Attachment
        {
            FileName = fileName,
            DocumentDescription = Path.GetFileNameWithoutExtension(filePath),
            FileContent = fileContent
        };
    }
}
