namespace AegisErp.Domain.Entities;

/// <summary>An AR customer. Invoices and receipts reference the customer to form its subledger.</summary>
public class Customer : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Human-facing customer number, e.g. "C-0001". Unique within the company.</summary>
    public string Code { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    // ── Identity ──
    public CustomerType CustomerType { get; set; } = CustomerType.Business;
    public string? Salutation { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? CompanyName { get; set; }

    /// <summary>Display name (primary language) — the name shown everywhere else in the app
    /// (invoices, statements, summaries). Required, same as it always has been.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Display name in the secondary language (Arabic), for bilingual documents. Optional.</summary>
    public string? DisplayNameArabic { get; set; }

    public string CustomerLanguage { get; set; } = "English";

    /// <summary>UAE Tax Registration Number (optional).</summary>
    public string? Trn { get; set; }

    public string? Email { get; set; }

    /// <summary>Kept for backward compatibility with existing reads/reports. New forms use
    /// <see cref="WorkPhone"/>/<see cref="Mobile"/> instead.</summary>
    public string? Phone { get; set; }
    public string? WorkPhone { get; set; }
    public string? Mobile { get; set; }

    public string? Address { get; set; }

    /// <summary>Customer group / segment, e.g. Corporate, Individual, Government.</summary>
    public string? Group { get; set; }

    /// <summary>Account currency. AED-only for now (stored, no FX logic).</summary>
    public string Currency { get; set; } = "AED";

    /// <summary>Credit limit in the account currency (0 = no limit set).</summary>
    public decimal CreditLimit { get; set; }

    /// <summary>Days until an invoice falls due (drives the invoice DueDate and aging).</summary>
    public int PaymentTermsDays { get; set; } = 30;

    /// <summary>Staff member who owns this customer relationship. Only collected when the owning
    /// company has <see cref="CompanySetup.SalespersonEnabled"/> turned on — some companies
    /// (e.g. pro-services firms) track this, most don't.</summary>
    public string? Salesperson { get; set; }

    /// <summary>Employee responsible for this customer's commission attribution — independent of, and
    /// additive to, <see cref="Salesperson"/>. See <see cref="CustomerAssignmentHistory"/> for how this
    /// changes over time.</summary>
    public int? AssignedEmployeeId { get; set; }
    public Employee? AssignedEmployee { get; set; }

    /// <summary>Third-party agent responsible for this customer's commission attribution — a customer
    /// may have an employee only, an agent only, both, or neither.</summary>
    public int? AssignedAgentId { get; set; }
    public Agent? AssignedAgent { get; set; }

    // ── Other Details ──
    /// <summary>UAE VAT treatment for this customer. Optional — existing customers predate this field.</summary>
    public TaxTreatment? TaxTreatment { get; set; }

    /// <summary>Emirate the supply is deemed to be made in, for VAT purposes.</summary>
    public string? PlaceOfSupply { get; set; }

    /// <summary>Informational only — recorded for reference, never posts a GL journal (same spirit
    /// as <see cref="Salesperson"/>, which is also just a tag with no automated effect).</summary>
    public decimal OpeningBalance { get; set; }

    /// <summary>Informational only — this app has no customer-facing portal behind it yet.</summary>
    public bool EnablePortal { get; set; }

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

    public List<CustomerContactPerson> ContactPersons { get; set; } = new();
    public List<CustomerDocument> Documents { get; set; } = new();
    public List<CustomerCustomFieldValue> CustomFieldValues { get; set; } = new();
    public List<CustomerTag> Tags { get; set; } = new();
    public List<SalespersonAssignmentHistory> SalespersonHistory { get; set; } = new();
    public List<CustomerOrganization> Organizations { get; set; } = new();
    public List<CustomerAssignmentHistory> AssignmentHistory { get; set; } = new();
}

/// <summary>One of this customer's separate trade licenses / legal entities — a PRO services
/// company processes government paperwork per specific license, and each one can carry its own
/// TRN distinct from the customer's own. Picking one on a document (Estimate/Sales Invoice) fills
/// the document's TRN from here instead of the customer's.</summary>
public class CustomerOrganization
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string? Trn { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>One person to contact at this customer — a customer can have several (billing contact,
/// operations contact, etc.), each optionally marked as the primary one used on documents.</summary>
public class CustomerContactPerson
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

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

/// <summary>One supporting document attached to a customer (contract, trade license, etc.). Unlike
/// the single-attachment pattern used on invoices/payments elsewhere, a customer can hold several —
/// count and size are capped in the service layer (<c>CustomerService.MaxDocumentsPerCustomer</c> /
/// <c>MaxDocumentBytes</c>).</summary>
public class CustomerDocument
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public DateTime UploadedAtUtc { get; set; }
}

/// <summary>An admin-defined extra field for a given module (e.g. "Customer"). Rendered dynamically
/// on that module's form; the actual per-record value is stored separately (see
/// <see cref="CustomerCustomFieldValue"/>) so the field can be added/removed without a migration.</summary>
public class CustomFieldDefinition : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    /// <summary>Which form this field appears on, e.g. "Customer". Kept as a plain string (not an
    /// enum) so new modules can adopt custom fields later without a domain change here.</summary>
    public string Module { get; set; } = "Customer";

    public string Label { get; set; } = string.Empty;
    public CustomFieldType FieldType { get; set; } = CustomFieldType.Text;

    /// <summary>Comma-separated options — only meaningful when <see cref="FieldType"/> is <see cref="CustomFieldType.Dropdown"/>.</summary>
    public string? DropdownOptionsCsv { get; set; }

    public bool IsRequired { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>The value a specific customer has entered for one <see cref="CustomFieldDefinition"/>.
/// Stored as a raw string regardless of the field's type — simplest representation for a v1 where
/// every type (text/number/date/dropdown/checkbox) round-trips fine as text.</summary>
public class CustomerCustomFieldValue
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public int CustomFieldDefinitionId { get; set; }
    public CustomFieldDefinition CustomFieldDefinition { get; set; } = null!;

    public string? Value { get; set; }
}

/// <summary>An admin-managed group of reporting tags (e.g. "Region", "Department") for a given
/// module. Each customer can pick at most one <see cref="Tag"/> from each active group.</summary>
public class TagGroup : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    /// <summary>Which form this tag group applies to, e.g. "Customer" — same reasoning as
    /// <see cref="CustomFieldDefinition.Module"/>.</summary>
    public string Module { get; set; } = "Customer";

    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public List<Tag> Tags { get; set; } = new();
}

/// <summary>One selectable value within a <see cref="TagGroup"/>.</summary>
public class Tag
{
    public int Id { get; set; }
    public int TagGroupId { get; set; }
    public TagGroup TagGroup { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A customer's chosen tag from one <see cref="TagGroup"/>. At most one row per
/// (CustomerId, TagGroupId) — enforced by the service, not the database.</summary>
public class CustomerTag
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public int TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}

/// <summary>
/// One change to a customer's <see cref="Customer.Salesperson"/> assignment. Logged by
/// <see cref="Services.CustomerService"/> whenever the value actually changes (including the very
/// first assignment, so a report never has to special-case "no history yet"). Exists so revenue can
/// later be attributed to whichever salesperson owned the customer on a given document's own date,
/// not whoever owns it today.
/// </summary>
public class SalespersonAssignmentHistory
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>Null means the customer had no salesperson before this change.</summary>
    public string? PreviousSalesperson { get; set; }

    /// <summary>Null means the salesperson was cleared/unassigned.</summary>
    public string? NewSalesperson { get; set; }

    public DateTime ChangedAtUtc { get; set; }
    public string ChangedBy { get; set; } = "System Admin";
}
