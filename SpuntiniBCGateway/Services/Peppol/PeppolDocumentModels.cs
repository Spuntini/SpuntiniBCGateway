using DocumentFormat.OpenXml.Presentation;

namespace SpuntiniBCGateway.Services;

/// <summary>
/// Enumeration of supported Peppol document types
/// </summary>
public enum PeppolDocumentType
{
    /// <summary>Invoice from supplier to customer (TypeCode 380)</summary>
    ClientInvoice = 380,

    /// <summary>Credit note from supplier to customer (TypeCode 381)</summary>
    ClientCreditNote = 381,

    /// <summary>Invoice from customer to supplier (TypeCode 380, but in reverse direction)</summary>
    VendorInvoice = 480,

    /// <summary>Credit note from customer to supplier (TypeCode 381, but in reverse direction)</summary>
    VendorCreditNote = 481
}

/// <summary>
/// Enumeration to identify party role in the document
/// </summary>
public enum PeppolPartyRole
{
    Supplier,
    Customer,
    Buyer,
    Seller
}



/// <summary>
/// Represents a Peppol UBL document header with metadata and party information
/// </summary>
public class PeppolDocumentHeader
{
    public string DocumentId { get; set; } = string.Empty;
    public string? IssueDate { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    public PeppolDocumentType DocumentType { get; set; } = PeppolDocumentType.ClientInvoice;
    public string DocumentTypeCode { get; set; } = string.Empty;
    public string CurrencyCode { get; set; } = "EUR";
    public string? DueDate { get; set; }
    public string? BuyerReference { get; set; }
    public string? OrderReference { get; set; }
    public string? DeliveryNoteReference { get; set; }
    public string? RelatedDocumentId { get; set; } // For credit notes: reference to original invoice

    // Supplier Info
    public PeppolParty SupplierParty { get; set; } = new();

    // Customer/Buyer Info
    public PeppolParty CustomerParty { get; set; } = new();

    // Delivery Info
    public PeppolDeliveryInfo? DeliveryInfo { get; set; }

    // Totals
    public decimal LineExtensionAmount { get; set; }
    public decimal TaxExclusiveAmount { get; set; }
    public decimal TaxInclusiveAmount { get; set; }
    public decimal PrepaidAmount { get; set; }
    public decimal DuePayableAmount { get; set; }

    // Tax Information
    public List<PeppolTaxTotal> TaxTotals { get; set; } = [];

    // Payment Terms
    public string? PaymentTermsNote { get; set; }
    public string? PaymentMeansCode { get; set; }
    public string? PaymentId { get; set; }

    // Attachments
    public Attachment? Attachment { get; set; }
}

/// <summary>
/// Represents a Peppol UBL document line (invoice line or credit note line)
/// </summary>
public class PeppolDocumentLine
{
    public string LineId { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string UnitCode { get; set; } = string.Empty;
    public decimal LineExtensionAmount { get; set; }
    public string AccountingCostCode { get; set; } = string.Empty;

    // Item Information
    public string ItemName { get; set; } = string.Empty;
    public string ItemDescription { get; set; } = string.Empty;
    public string? ItemCode { get; set; }
    public decimal UnitPrice { get; set; }
    public string? CommodityCode { get; set; }

    // Tax Information
    public List<PeppolLineTax> TaxCategories { get; set; } = [];
}

/// <summary>
/// Represents party information (supplier or customer)
/// </summary>
public class PeppolParty
{
    public string Name { get; set; } = string.Empty;
    public string? RegistrationName { get; set; }
    public string? CompanyId { get; set; }
    public string? VatRegistrationId { get; set; }
    public string? ElectronicMail { get; set; }

    // Address
    public PeppolAddress? PostalAddress { get; set; }

    // Contact
    public PeppolContact? Contact { get; set; }
}

/// <summary>
/// Represents address information
/// </summary>
public class PeppolAddress
{
    public string? StreetName { get; set; }
    public string? BuildingNumber { get; set; }
    public string? CityName { get; set; }
    public string? PostalZone { get; set; }
    public string? CountryCode { get; set; }
}

/// <summary>
/// Represents contact information
/// </summary>
public class PeppolContact
{
    public string? Name { get; set; }
    public string? Telephone { get; set; }
    public string? ElectronicMail { get; set; }
}

/// <summary>
/// Represents delivery information
/// </summary>
public class PeppolDeliveryInfo
{
    public string? ActualDeliveryDate { get; set; }
    public PeppolAddress? DeliveryAddress { get; set; }
}

/// <summary>
/// Represents tax information for an invoice
/// </summary>
public class PeppolTaxTotal
{
    public decimal TaxAmount { get; set; }
    public string CurrencyCode { get; set; } = "EUR";
    public List<PeppolTaxSubtotal> TaxSubtotals { get; set; } = [];
}

/// <summary>
/// Represents tax subtotal (per tax category)
/// </summary>
public class PeppolTaxSubtotal
{
    public decimal TaxableAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public string TaxCategoryId { get; set; } = string.Empty;
    public decimal TaxPercent { get; set; }
    public string TaxSchemeId { get; set; } = "VAT";
}

/// <summary>
/// Represents tax information for an invoice line
/// </summary>
public class PeppolLineTax
{
    public string TaxCategoryId { get; set; } = string.Empty;
    public decimal TaxPercent { get; set; }
    public string TaxSchemeId { get; set; } = "VAT";
}

/// <summary>
/// Complete Peppol UBL Document (Invoice, Credit Note, etc.)
/// </summary>
public class PeppolDocument
{
    public PeppolDocumentHeader Header { get; set; } = new();
    public List<PeppolDocumentLine> DocumentLines { get; set; } = [];

    public bool ForceUpdate = false;
}
