namespace AegisErp.Domain.Entities;

/// <summary>An AP vendor (supplier). Purchase invoices and payments reference the vendor to form its
/// subledger. Field set deliberately mirrors <see cref="Customer"/> (identity, contact persons,
/// billing/shipping address, tax treatment, custom fields, tags, documents) — the same information
/// is worth capturing on both sides of a transaction, minus the AR-only concepts (Salesperson,
/// EnablePortal) that have no AP equivalent.</summary>
public class Vendor : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Human-facing vendor number, e.g. "V-0001". Unique within the company.</summary>
    public string Code { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    // ── Identity ──
    public CustomerType VendorType { get; set; } = CustomerType.Business;
    public string? Salutation { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? CompanyName { get; set; }

    /// <summary>Display name — shown everywhere else in the app (bills, payments, statements).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Display name in the secondary language (Arabic), for bilingual documents. Optional.</summary>
    public string? DisplayNameArabic { get; set; }

    public string VendorLanguage { get; set; } = "English";

    /// <summary>UAE Tax Registration Number (optional).</summary>
    public string? Trn { get; set; }

    public string? Email { get; set; }

    /// <summary>Kept for backward compatibility with existing reads/reports. New forms use
    /// <see cref="WorkPhone"/>/<see cref="Mobile"/> instead.</summary>
    public string? Phone { get; set; }
    public string? WorkPhone { get; set; }
    public string? Mobile { get; set; }

    public string? Address { get; set; }

    /// <summary>Vendor group / category, e.g. Supplier, Contractor, Utility, Government.</summary>
    public string? Group { get; set; }

    /// <summary>Account currency. AED-only for now (stored, no FX logic).</summary>
    public string Currency { get; set; } = "AED";

    /// <summary>Credit the vendor extends to us, in the account currency (0 = not tracked).</summary>
    public decimal CreditLimit { get; set; }

    /// <summary>Days until a purchase invoice falls due (drives the invoice DueDate and aging).</summary>
    public int PaymentTermsDays { get; set; } = 30;

    // ── Other Details ──
    /// <summary>UAE VAT treatment for this vendor. Optional — existing vendors predate this field.</summary>
    public TaxTreatment? TaxTreatment { get; set; }

    /// <summary>Emirate the supply is deemed to be made in, for VAT purposes.</summary>
    public string? PlaceOfSupply { get; set; }

    /// <summary>Informational only — recorded for reference, never posts a GL journal.</summary>
    public decimal OpeningBalance { get; set; }

    public string? Remarks { get; set; }

    // ── Billing Address ──
    public string? BillingAttention { get; set; }
    public string? BillingCountry { get; set; }
    public string? BillingAddressLine1 { get; set; }
    public string? BillingAddressLine1Arabic { get; set; }
    public string? BillingAddressLine2 { get; set; }
    public string? BillingAddressLine2Arabic { get; set; }
    public string? BillingCity { get; set; }
    public string? BillingEmirate { get; set; }
    public string? BillingZip { get; set; }
    public string? BillingPhone { get; set; }
    public string? BillingFax { get; set; }

    // ── Shipping Address ──
    public string? ShippingAttention { get; set; }
    public string? ShippingCountry { get; set; }
    public string? ShippingAddressLine1 { get; set; }
    public string? ShippingAddressLine1Arabic { get; set; }
    public string? ShippingAddressLine2 { get; set; }
    public string? ShippingAddressLine2Arabic { get; set; }
    public string? ShippingCity { get; set; }
    public string? ShippingEmirate { get; set; }
    public string? ShippingZip { get; set; }
    public string? ShippingPhone { get; set; }
    public string? ShippingFax { get; set; }

    public bool IsActive { get; set; } = true;

    public List<VendorContactPerson> ContactPersons { get; set; } = new();
    public List<VendorDocument> Documents { get; set; } = new();
    public List<VendorCustomFieldValue> CustomFieldValues { get; set; } = new();
    public List<VendorTag> Tags { get; set; } = new();
}

/// <summary>One person to contact at this vendor — mirrors <see cref="CustomerContactPerson"/>.</summary>
public class VendorContactPerson
{
    public int Id { get; set; }
    public int VendorId { get; set; }
    public Vendor Vendor { get; set; } = null!;

    public string? Salutation { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? WorkPhone { get; set; }
    public string? Mobile { get; set; }
    public string? Designation { get; set; }
    public string? Department { get; set; }
    public bool IsPrimary { get; set; }
}

/// <summary>One supporting document attached to a vendor (trade license, contract, etc.) — mirrors
/// <see cref="CustomerDocument"/>.</summary>
public class VendorDocument
{
    public int Id { get; set; }
    public int VendorId { get; set; }
    public Vendor Vendor { get; set; } = null!;

    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public DateTime UploadedAtUtc { get; set; }
}

/// <summary>The value a specific vendor has entered for one <see cref="CustomFieldDefinition"/>
/// (Module = "Vendor") — mirrors <see cref="CustomerCustomFieldValue"/>, reusing the same generic
/// admin-defined field infrastructure.</summary>
public class VendorCustomFieldValue
{
    public int Id { get; set; }
    public int VendorId { get; set; }
    public Vendor Vendor { get; set; } = null!;

    public int CustomFieldDefinitionId { get; set; }
    public CustomFieldDefinition CustomFieldDefinition { get; set; } = null!;

    public string? Value { get; set; }
}

/// <summary>A vendor's chosen tag from one <see cref="TagGroup"/> (Module = "Vendor") — mirrors
/// <see cref="CustomerTag"/>. At most one row per (VendorId, TagGroupId), enforced by the service.</summary>
public class VendorTag
{
    public int Id { get; set; }
    public int VendorId { get; set; }
    public Vendor Vendor { get; set; } = null!;

    public int TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}
