namespace AegisErp.Domain.Entities;

/// <summary>
/// Company-wide configuration (a single row). Captures the "Create Company" requirement:
/// general info, financial setup, localization, address, VAT, corporate tax, tax config,
/// system controls and bank accounts. Most fields are stored configuration; a few
/// (base currency, TRN, financial year) drive real behaviour as those features are built.
/// </summary>
public class CompanySetup
{
    public int Id { get; set; }

    // ── 1. General Information ──
    public string LegalName { get; set; } = string.Empty;      // Company Name (Legal) *
    public string? TradeName { get; set; }
    public string CompanyCode { get; set; } = string.Empty;    // auto, unique
    public string? LicenseNumber { get; set; }                 // *
    public string? LicenseType { get; set; }                   // LLC / FZE / Sole Establishment
    public DateOnly? RegistrationDate { get; set; }            // *
    public DateOnly? LicenseExpiryDate { get; set; }
    public string? Country { get; set; } = "United Arab Emirates";
    public string? Emirate { get; set; }
    public string? PlaceOfIncorporation { get; set; }
    public bool IsFreeZone { get; set; }
    public bool IsDesignatedZone { get; set; }                 // only if IsFreeZone

    // ── 2. Financial Setup ──
    public DateOnly? FinancialYearStart { get; set; }          // *
    public DateOnly? FinancialYearEnd { get; set; }            // auto (start + 1 year - 1 day)
    public DateOnly? BooksStartDate { get; set; }              // *
    public string AccountingMethod { get; set; } = "Accrual";  // Accrual / Cash
    public string? FiscalYear { get; set; }                    // e.g. "Jan–Dec"
    public string BaseCurrency { get; set; } = "AED";          // *
    public string? ReportingCurrency { get; set; }

    // ── 3. Localization & Language ──
    public string? OrganizationLanguage { get; set; }
    public string? CommunicationLanguage { get; set; }         // multi-select stored as CSV
    public string? InvoiceLanguage { get; set; }
    public string? TimeZone { get; set; } = "Asia/Dubai";
    public string? DateFormat { get; set; } = "dd/MM/yyyy";

    // ── 4. Address (Registered) ──
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? AddressEmirate { get; set; }
    public string? POBox { get; set; }
    public string? AddressCountry { get; set; } = "United Arab Emirates";
    public string? Phone { get; set; }
    public string? Fax { get; set; }

    // ── 4b. Billing Address ──
    public bool BillingSameAsRegistered { get; set; } = true;
    public string? BillingAddressLine1 { get; set; }
    public string? BillingAddressLine2 { get; set; }
    public string? BillingCity { get; set; }
    public string? BillingEmirate { get; set; }
    public string? BillingPOBox { get; set; }
    public string? BillingCountry { get; set; }

    // ── 5. VAT Setup ──
    public bool VatRegistered { get; set; }
    public string TrnLabel { get; set; } = "TRN";              // TRN / GST
    public string? TrnNumber { get; set; }                     // 15-digit
    public DateOnly? VatRegistrationDate { get; set; }
    public string? VatScheme { get; set; }                     // Standard / Cash
    public string? VatFilingFrequency { get; set; }            // Monthly / Quarterly
    public DateOnly? VatDeregistrationDate { get; set; }

    // ── 6. Corporate Tax ──
    public bool CtRegistered { get; set; }
    public string? CtTrn { get; set; }
    public DateOnly? FirstTaxPeriodStart { get; set; }
    public bool FreeZonePerson { get; set; }
    public bool QfzpStatus { get; set; }
    public bool SmallBusinessRelief { get; set; }

    // ── 7. Tax Configuration ──
    public decimal DefaultVatRate { get; set; } = 5m;          // %
    public string? InputVatAccountCode { get; set; }           // GL mapping
    public string? OutputVatAccountCode { get; set; }          // GL mapping

    // ── 7b. Invoice Preferences ──
    public int InvoiceDefaultPaymentTermsDays { get; set; } = 30;
    public string? InvoiceDefaultNotes { get; set; }
    /// <summary>Pre-filled into the separate Terms &amp; Conditions box on every new invoice — still editable per invoice.</summary>
    public string? InvoiceDefaultTermsAndConditions { get; set; }
    public string InvoiceNumberPrefix { get; set; } = "INV";

    /// <summary>Send an automated reminder this many days before the due date (0 = don't).</summary>
    public int ReminderDaysBeforeDue { get; set; }
    public bool ReminderOnDueDate { get; set; }
    /// <summary>Comma-separated days-after-due-date to remind on, e.g. "7,15,30" — same CSV-list
    /// convention already used for <see cref="CommunicationLanguage"/>.</summary>
    public string? ReminderDaysAfterDue { get; set; } = "7,15,30";

    // ── 8. System Controls ──
    public bool MultiCompanyEnabled { get; set; }
    public bool AuditTrailEnabled { get; set; } = true;
    public bool ApprovalWorkflowEnabled { get; set; }

    /// <summary>When on, the New Customer form and Sales Invoice both collect a Salesperson — on by
    /// default per the client's request, but still a per-company switch (Admin-only; this whole page
    /// is <c>[Authorize(Roles = AppRoles.Admins)]</c>) for any company that doesn't track this.</summary>
    public bool SalespersonEnabled { get; set; } = true;

    /// <summary>When on, Sales Invoice shows a header-level Direct/Deferred revenue recognition
    /// choice that applies to every line — off by default, in which case every line simply posts
    /// as Direct without asking. A company recognising revenue over time (subscriptions, retainers)
    /// would turn this on at setup instead of picking Recognition per line on every invoice.</summary>
    public bool RevenueRecognitionEnabled { get; set; }

    /// <summary>When on, Purchase Invoice, Estimate and Sales Invoice all switch to the "PRO
    /// Service" layout: one Cost Center for the whole document instead of per line, and each line
    /// is a flat Govt Fee / Center Fee / Bank Charge breakdown instead of Quantity × Unit Price —
    /// off by default, in which case all three documents are unchanged from their standard
    /// goods-style entry. A PRO services company (visa/labour/government paperwork processing)
    /// would turn this on, since its documents are almost always flat government/bank fees plus a
    /// flat service charge with no quantity-based pricing involved.</summary>
    public bool ProServiceModeEnabled { get; set; }

    /// <summary>13-digit MOHRE Establishment ID, required on every UAE WPS SIF salary file line —
    /// validated at export time (see <c>WpsFileService</c>), not here, since a company might
    /// configure payroll before actually registering for WPS.</summary>
    public string? MohreEstablishmentId { get; set; }

    /// <summary>9-digit routing code of the bank/exchange house submitting the WPS file on the
    /// company's behalf — goes on the SCR (Salary Control Record) header line. Distinct from each
    /// <see cref="Employee.WpsAgentId"/>, which identifies the bank receiving that employee's own
    /// salary and can differ employee to employee.</summary>
    public string? WpsBankAgentId { get; set; }

    // ── Branding ──
    /// <summary>The company's own logo, shown on its printed Sales Invoices, Estimates and
    /// Payslips (see the printout components). Optional — those documents render without one when
    /// null, same as they always have.</summary>
    public string? LogoFileName { get; set; }
    public string? LogoContentType { get; set; }
    public byte[]? LogoData { get; set; }

    /// <summary>The company's maintained list of salesperson names — admin-curated here, offered
    /// as a dropdown on the New Customer form instead of free text, so names stay consistent.</summary>
    public List<Salesperson> Salespersons { get; set; } = new();

    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>Optimistic-concurrency token. Regenerated on every update; the save fails if the
    /// value the editor started from no longer matches what's in the database.</summary>
    public Guid RowVersion { get; set; } = Guid.NewGuid();

    public List<CompanyBankAccount> BankAccounts { get; set; } = new();
    public List<CompanySetupDocument> Documents { get; set; } = new();

    // ── Subscription billing (firm → this client company; see [[SubscriptionStatusCalculator]]) ──

    /// <summary>When false (the default), this company's access is never gated by billing — every
    /// company stays this way until a firm administrator deliberately opts it into enforcement, so
    /// turning the feature on can never retroactively lock out a company that was never set up
    /// for it.</summary>
    public bool SubscriptionEnabled { get; set; }

    /// <summary>What this company is billed per cycle. Informational — recording a payment does not
    /// require it to match, since a firm administrator may agree a different amount with a given
    /// client.</summary>
    public decimal? SubscriptionAmount { get; set; }

    /// <summary>Days past <see cref="SubscriptionPaidThroughDate"/> before access is actually
    /// blocked for non-firm-administrators. Defaults to 5 so a payment landing a day or two late
    /// doesn't lock a client out instantly.</summary>
    public int SubscriptionGraceDays { get; set; } = 5;

    /// <summary>The date this company's billing is paid through. Null means never paid — advanced
    /// only by <c>SubscriptionService.RecordPaymentAsync</c>, never edited directly, so it always
    /// reflects the recorded payment history.</summary>
    public DateOnly? SubscriptionPaidThroughDate { get; set; }

    public List<CompanySubscriptionPayment> SubscriptionPayments { get; set; } = new();
}

/// <summary>One recorded subscription payment against a company (see
/// <see cref="CompanySetup.SubscriptionPaidThroughDate"/>). The firm administrator logs these by
/// hand after receiving payment through whatever channel the client actually pays by (bank
/// transfer, cheque, cash) — this is a record of the outcome, not a payment gateway.</summary>
public class CompanySubscriptionPayment
{
    public int Id { get; set; }
    public int CompanySetupId { get; set; }
    public CompanySetup CompanySetup { get; set; } = null!;

    public DateOnly PaymentDate { get; set; }
    public decimal Amount { get; set; }

    /// <summary>The date this specific payment advances <see cref="CompanySetup.SubscriptionPaidThroughDate"/>
    /// to. Recorded per payment (rather than only the running total) so the history reads like a
    /// statement: what was paid, when, and through when it covers.</summary>
    public DateOnly CoversThrough { get; set; }

    public string? Notes { get; set; }
    public string RecordedBy { get; set; } = "System Admin";
    public DateTime RecordedAtUtc { get; set; }
}

/// <summary>One uploaded file against a required UAE document slot (Trade License, MOA, VAT
/// Certificate, etc. — see <c>CompanySetupPage.RequiredDocs</c>). At most one row per
/// (CompanySetupId, DocType); re-uploading replaces the previous file.</summary>
public class CompanySetupDocument
{
    public int Id { get; set; }
    public int CompanySetupId { get; set; }
    public CompanySetup CompanySetup { get; set; } = null!;

    public string DocType { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public DateTime UploadedAtUtc { get; set; }
}

/// <summary>A company bank account (primary + additional). Section 9 of the requirement.</summary>
public class CompanyBankAccount
{
    public int Id { get; set; }
    public int CompanySetupId { get; set; }
    public CompanySetup CompanySetup { get; set; } = null!;

    public string BankName { get; set; } = string.Empty;
    public string? AccountName { get; set; }
    public string? AccountNumber { get; set; }
    public string? Iban { get; set; }
    public string? Swift { get; set; }
    public string Currency { get; set; } = "AED";
    public bool IsPrimary { get; set; }
}

/// <summary>One name in the company's maintained salesperson list (see
/// <see cref="CompanySetup.SalespersonEnabled"/>). Deliberately just a name — not tied to a login
/// user — since not every salesperson needs system access.</summary>
public class Salesperson
{
    public int Id { get; set; }
    public int CompanySetupId { get; set; }
    public CompanySetup CompanySetup { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
}
