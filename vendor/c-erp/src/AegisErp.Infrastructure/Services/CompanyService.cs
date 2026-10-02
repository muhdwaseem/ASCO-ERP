using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>A required document slot's upload state (see <see cref="CompanyService.GetDocumentsAsync"/>
/// to list, <see cref="CompanyService.GetDocumentAsync"/> to download).</summary>
public record CompanySetupDocumentInfo(int Id, string DocType, string FileName, string ContentType, long SizeBytes, DateTime UploadedAtUtc);

/// <summary>Loads and saves company records. Reads/writes the user's <em>active</em> company.</summary>
public class CompanyService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;

    public const int MaxDocumentBytes = 10 * 1024 * 1024;
    public const int MaxLogoBytes = 2 * 1024 * 1024;

    /// <summary>Content types accepted for a company logo — it's rendered straight into an
    /// <c>&lt;img&gt;</c> on printed documents, so only actual raster/vector image types are
    /// accepted rather than any file a picker happens to allow through.</summary>
    private static readonly string[] AllowedLogoContentTypes = { "image/png", "image/jpeg", "image/svg+xml" };

    public CompanyService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    /// <summary>Every company in the system (firm-wide; used by the switcher and companies page).</summary>
    public async Task<List<CompanySetup>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CompanySetups.AsNoTracking().OrderBy(c => c.LegalName).ToListAsync();
    }

    public async Task<CompanySetup?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CompanySetups.AsNoTracking().Include(c => c.BankAccounts).Include(c => c.Salespersons)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    /// <summary>Creates a new company (client). Company code must be unique. Firm-wide, not scoped
    /// to any one company, so this checks IsFirmAdmin rather than CanAdminister — the same
    /// distinction CompanySetupPage's own <c>_isNew</c> branch makes in the UI.</summary>
    public async Task<CompanySetup> CreateAsync(CompanySetup model)
    {
        if (!_current.IsFirmAdmin)
            throw new PostingException("Only a firm administrator can create companies.");
        if (string.IsNullOrWhiteSpace(model.LegalName))
            throw new PostingException("Company legal name is required.");

        var code = (model.CompanyCode ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code))
            throw new PostingException("Company code is required.");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.CompanySetups.AnyAsync(c => c.CompanyCode == code))
            throw new PostingException($"A company with code {code} already exists.");

        model.CompanyCode = code;
        db.CompanySetups.Add(model);
        await db.SaveChangesAsync(); // assigns model.Id

        // A company with no fiscal periods can never post anything and its dashboard has nothing
        // to show — so give it its first year up front instead of leaving that as a manual step.
        // Scoping this context to the just-created company (rather than whatever company the
        // FirmAdmin happened to have active) mirrors how SeedData builds a specific company's
        // books; ApplyCompanyScope would otherwise reject these rows as a cross-company write.
        db.CurrentCompanyId = model.Id;
        if (model.FinancialYearStart is DateOnly yearStart)
            db.FiscalPeriods.AddRange(FiscalPeriodService.BuildMonthlyYear(yearStart));

        // Same problem, different symptom: every posting flow requires a handful of control
        // accounts by exact code (AR, AP, VAT Payable/Input, Deferred Revenue, Equity), and Receipt/
        // Payment Voucher and Direct Expense additionally need at least one bank/cash account (code
        // starting "110") to deposit into or pay from — with none of these, the very first invoice,
        // bill or receipt fails outright instead of getting the friendly guidance the rest of
        // onboarding gives.
        db.Accounts.AddRange(ChartOfAccountsService.BuildStarterAccounts());

        await db.SaveChangesAsync();
        return model;
    }

    /// <summary>The active company's setup record.</summary>
    public async Task<CompanySetup> GetAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var query = db.CompanySetups.AsNoTracking().Include(c => c.BankAccounts).Include(c => c.Salespersons).AsQueryable();

        var company = _current.CompanyId is int id
            ? await query.FirstOrDefaultAsync(c => c.Id == id)
            : await query.FirstOrDefaultAsync();

        return company ?? throw new PostingException(
            "No company is selected. Pick a company before opening company setup.");
    }

    /// <summary>
    /// Saves the company's setup. <paramref name="model"/> must carry the RowVersion it was
    /// loaded with (from <see cref="GetAsync"/>) — if another user has saved a change since, this
    /// throws a recoverable <see cref="PostingException"/> instead of silently overwriting it.
    /// </summary>
    public async Task SaveAsync(CompanySetup model, string updatedBy)
    {
        if (!_current.CanAdminister)
            throw new PostingException("Only an Admin can edit this company's setup.");

        await using var db = await _dbf.CreateDbContextAsync();
        var existing = await db.CompanySetups.Include(c => c.BankAccounts).Include(c => c.Salespersons)
            .FirstOrDefaultAsync(c => c.Id == model.Id);

        if (existing is null)
        {
            db.CompanySetups.Add(model);
            await db.SaveChangesAsync();
            return;
        }

        var expectedRowVersion = model.RowVersion;

        // Copy scalar fields; then rebuild the bank-account and salesperson lists.
        db.Entry(existing).CurrentValues.SetValues(model);
        db.CompanyBankAccounts.RemoveRange(existing.BankAccounts);
        existing.BankAccounts = model.BankAccounts.Select(b => new CompanyBankAccount
        {
            BankName = b.BankName,
            AccountName = b.AccountName,
            AccountNumber = b.AccountNumber,
            Iban = b.Iban,
            Swift = b.Swift,
            Currency = string.IsNullOrWhiteSpace(b.Currency) ? "AED" : b.Currency,
            IsPrimary = b.IsPrimary,
        }).ToList();

        db.Salespersons.RemoveRange(existing.Salespersons);
        existing.Salespersons = model.Salespersons
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => new Salesperson { Name = s.Name.Trim() })
            .ToList();

        existing.UpdatedBy = updatedBy;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        existing.RowVersion = Guid.NewGuid();
        // Check against the version the editor actually loaded, not existing's freshly-read value
        // (SetValues above only touches CurrentValues; OriginalValues still holds the DB's current
        // row, which would defeat the check unless overridden here).
        db.Entry(existing).Property(c => c.RowVersion).OriginalValue = expectedRowVersion;

        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    // ── Branding ──

    /// <summary>Uploads (or replaces) the active company's logo. CanAdminister-gated server-side —
    /// mirrors <see cref="SaveAsync"/> — since this changes what appears on every printed document
    /// the company sends out, not something a Viewer/Accountant should be able to swap.</summary>
    public async Task UpdateLogoAsync(string fileName, string contentType, byte[] data)
    {
        if (!_current.CanAdminister)
            throw new PostingException("Only an Admin can change the company logo.");
        if (data.Length > MaxLogoBytes)
            throw new PostingException($"'{fileName}' is too large — the limit is {MaxLogoBytes / (1024 * 1024)} MB.");
        if (!AllowedLogoContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            throw new PostingException("Logo must be a PNG, JPG, or SVG image.");

        await using var db = await _dbf.CreateDbContextAsync();
        var company = _current.CompanyId is int id
            ? await db.CompanySetups.FirstOrDefaultAsync(c => c.Id == id)
            : await db.CompanySetups.FirstOrDefaultAsync();
        if (company is null)
            throw new PostingException("No company is selected.");

        company.LogoFileName = fileName;
        company.LogoContentType = contentType;
        company.LogoData = data;
        await db.SaveChangesAsync();
    }

    public async Task RemoveLogoAsync()
    {
        if (!_current.CanAdminister)
            throw new PostingException("Only an Admin can change the company logo.");

        await using var db = await _dbf.CreateDbContextAsync();
        var company = _current.CompanyId is int id
            ? await db.CompanySetups.FirstOrDefaultAsync(c => c.Id == id)
            : await db.CompanySetups.FirstOrDefaultAsync();
        if (company is null)
            throw new PostingException("No company is selected.");

        company.LogoFileName = null;
        company.LogoContentType = null;
        company.LogoData = null;
        await db.SaveChangesAsync();
    }

    // ── Documents ──

    public async Task<List<CompanySetupDocumentInfo>> GetDocumentsAsync(int companySetupId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CompanySetupDocuments.AsNoTracking()
            .Where(d => d.CompanySetupId == companySetupId)
            .OrderBy(d => d.DocType)
            .Select(d => new CompanySetupDocumentInfo(d.Id, d.DocType, d.FileName, d.ContentType, d.Data.Length, d.UploadedAtUtc))
            .ToListAsync();
    }

    /// <summary>Uploads (or replaces, if one already exists for this slot) the file for a required
    /// document type.</summary>
    public async Task<CompanySetupDocument> UploadDocumentAsync(int companySetupId, string docType, string fileName, string contentType, byte[] data, DateTime nowUtc)
    {
        if (data.Length > MaxDocumentBytes)
            throw new PostingException($"'{fileName}' is too large — the limit is {MaxDocumentBytes / (1024 * 1024)} MB per file.");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.CompanySetups.FindAsync(companySetupId) is null)
            throw new PostingException("Company not found.");

        var doc = await db.CompanySetupDocuments.FirstOrDefaultAsync(d => d.CompanySetupId == companySetupId && d.DocType == docType);
        if (doc is null)
        {
            doc = new CompanySetupDocument { CompanySetupId = companySetupId, DocType = docType };
            db.CompanySetupDocuments.Add(doc);
        }

        doc.FileName = fileName;
        doc.ContentType = contentType;
        doc.Data = data;
        doc.UploadedAtUtc = nowUtc;

        await db.SaveChangesAsync();
        return doc;
    }

    public async Task RemoveDocumentAsync(int companySetupId, int documentId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var doc = await db.CompanySetupDocuments.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanySetupId == companySetupId)
            ?? throw new PostingException("Document not found.");
        db.CompanySetupDocuments.Remove(doc);
        await db.SaveChangesAsync();
    }

    public async Task<(string FileName, string ContentType, byte[] Data)?> GetDocumentAsync(int companySetupId, int documentId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var doc = await db.CompanySetupDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanySetupId == companySetupId);
        return doc is null ? null : (doc.FileName, doc.ContentType, doc.Data);
    }
}
