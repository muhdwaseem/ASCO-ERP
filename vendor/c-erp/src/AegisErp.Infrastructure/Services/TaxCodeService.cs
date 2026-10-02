using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Everything the "New Tax Code" form collects.</summary>
public record NewTaxCodeInput(
    string Code, string Description, decimal Rate, VatTaxType TaxType, int? GlAccountId, DateOnly EffectiveFrom,
    int? OutputAccountId = null, int? InputAccountId = null);

/// <summary>Status of one of the five standard VAT control accounts on the VAT Master's Accounts
/// Mapping tab — whether it exists yet in this company's Chart of Accounts, by code.</summary>
public record VatControlAccountStatus(
    string Code, string Name, string Description, AccountType AccountType,
    int? MappedAccountId, string? MappedAccountLabel)
{
    public bool IsMapped => MappedAccountId is not null;
}

public class TaxCodeService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ChartOfAccountsService _coa;
    private readonly ICurrentCompany _current;
    public TaxCodeService(IDbContextFactory<AegisDbContext> dbf, ChartOfAccountsService coa, ICurrentCompany current)
    {
        _dbf = dbf;
        _coa = coa;
        _current = current;
    }

    /// <summary>The five standard VAT control accounts the VAT Master tracks and can
    /// auto-configure — code, display name, description, and the account type it should be
    /// created as if missing. <see cref="WellKnownAccounts.VatPayable"/>/<see cref="WellKnownAccounts.VatInput"/>
    /// are the two this app has always posted to; the other three exist so a Reverse Charge code
    /// and the periodic VAT return have somewhere to point, even though nothing posts to them yet.</summary>
    private static readonly (string Code, string Name, string Description, AccountType Type)[] ControlAccounts =
    {
        (WellKnownAccounts.VatPayable, "Output VAT (Payable)", "VAT collected on sales — owed to the FTA.", AccountType.Liability),
        (WellKnownAccounts.VatInput, "Input VAT (Recoverable)", "VAT paid on purchases — reclaimable from the FTA.", AccountType.Asset),
        (WellKnownAccounts.VatControl, "VAT Payable (Control)", "Net settlement account (Output − Input) for the VAT return.", AccountType.Liability),
        (WellKnownAccounts.ReverseChargeVatPayable, "Reverse Charge VAT Payable", "Payable leg for reverse-charge imports (RCM).", AccountType.Liability),
        (WellKnownAccounts.ReverseChargeVatRecoverable, "Reverse Charge VAT Recoverable", "Recoverable leg for reverse-charge imports (RCM).", AccountType.Asset),
    };

    /// <summary>VAT tax types that actually charge VAT — a code with one of these is what
    /// <see cref="AutoConfigureAsync"/> maps to a real account; <see cref="VatTaxType.ZeroRated"/>
    /// and <see cref="VatTaxType.Exempt"/> charge nothing, so they're left unmapped.</summary>
    private static readonly VatTaxType[] ChargingTaxTypes = { VatTaxType.StandardRated, VatTaxType.ReducedRate, VatTaxType.ReverseCharge };

    public async Task<List<TaxCode>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.TaxCodes.AsNoTracking()
            .Include(t => t.GlAccount).Include(t => t.OutputAccount).Include(t => t.InputAccount)
            .OrderBy(t => t.Code).ToListAsync();
    }

    public async Task<TaxCode> AddAsync(NewTaxCodeInput input)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        var code = input.Code.Trim().ToUpperInvariant();
        var description = input.Description.Trim();
        if (string.IsNullOrWhiteSpace(code)) throw new PostingException("Tax code is required.");
        if (string.IsNullOrWhiteSpace(description)) throw new PostingException("Description is required.");
        if (input.Rate < 0) throw new PostingException("Rate cannot be negative.");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.TaxCodes.AnyAsync(t => t.Code == code))
            throw new PostingException($"Tax code {code} already exists.");

        var tax = new TaxCode
        {
            Code = code,
            Description = description,
            Rate = input.Rate,
            TaxType = input.TaxType,
            GlAccountId = input.GlAccountId,
            OutputAccountId = input.OutputAccountId,
            InputAccountId = input.InputAccountId,
            EffectiveFrom = input.EffectiveFrom,
        };
        db.TaxCodes.Add(tax);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return tax;
    }

    /// <summary>Replaces a tax code's fields in place — safe to change freely, including Rate and GL
    /// Account, because sales/purchase lines snapshot their own VAT rate at posting time rather than
    /// referencing the tax code live, so editing it never rewrites already-posted history.</summary>
    public async Task<TaxCode> UpdateAsync(int id, NewTaxCodeInput input)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        var code = input.Code.Trim().ToUpperInvariant();
        var description = input.Description.Trim();
        if (string.IsNullOrWhiteSpace(code)) throw new PostingException("Tax code is required.");
        if (string.IsNullOrWhiteSpace(description)) throw new PostingException("Description is required.");
        if (input.Rate < 0) throw new PostingException("Rate cannot be negative.");

        await using var db = await _dbf.CreateDbContextAsync();
        var tax = await db.TaxCodes.FirstOrDefaultAsync(t => t.Id == id)
            ?? throw new PostingException("Tax code not found.");
        if (await db.TaxCodes.AnyAsync(t => t.Id != id && t.Code == code))
            throw new PostingException($"Tax code {code} already exists.");

        tax.Code = code;
        tax.Description = description;
        tax.Rate = input.Rate;
        tax.TaxType = input.TaxType;
        tax.GlAccountId = input.GlAccountId;
        tax.OutputAccountId = input.OutputAccountId;
        tax.InputAccountId = input.InputAccountId;
        tax.EffectiveFrom = input.EffectiveFrom;
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return tax;
    }

    /// <summary>Sets just a code's Output/Input VAT account mapping, from the VAT Master's Accounts
    /// Mapping tab — leaves every other field (including the legacy <see cref="TaxCode.GlAccountId"/>)
    /// untouched.</summary>
    public async Task SetAccountMappingAsync(int id, int? outputAccountId, int? inputAccountId)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var tax = await db.TaxCodes.FirstOrDefaultAsync(t => t.Id == id)
            ?? throw new PostingException("Tax code not found.");

        tax.OutputAccountId = outputAccountId;
        tax.InputAccountId = inputAccountId;
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    public async Task SetActiveAsync(int id, bool isActive)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var tax = await db.TaxCodes.FirstOrDefaultAsync(t => t.Id == id)
            ?? throw new PostingException("Tax code not found.");
        tax.IsActive = isActive;
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    public async Task DeleteAsync(int id)
    {
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        await using var db = await _dbf.CreateDbContextAsync();
        var tax = await db.TaxCodes.FirstOrDefaultAsync(t => t.Id == id)
            ?? throw new PostingException("Tax code not found.");

        // Checked explicitly rather than relying on the database's FK constraint to reject the
        // delete, so the user gets a clear reason instead of a raw "something went wrong" from an
        // unhandled DbUpdateException.
        if (await db.Items.AnyAsync(i => i.TaxCodeId == id))
            throw new PostingException("This tax code is used by an item and cannot be deleted — deactivate it instead.");

        db.TaxCodes.Remove(tax);
        await db.SaveChangesAsync();
    }

    /// <summary>Whether each of the five standard VAT control accounts (see
    /// <see cref="ControlAccounts"/>) already exists in this company's Chart of Accounts, by code —
    /// backs the VAT Master's Accounts Mapping status rows.</summary>
    public async Task<List<VatControlAccountStatus>> GetControlAccountStatusAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var codes = ControlAccounts.Select(c => c.Code).ToList();
        var existing = await db.Accounts.AsNoTracking()
            .Where(a => codes.Contains(a.Code))
            .ToDictionaryAsync(a => a.Code, a => a);

        return ControlAccounts.Select(c =>
        {
            existing.TryGetValue(c.Code, out var acc);
            return new VatControlAccountStatus(c.Code, c.Name, c.Description, c.Type,
                acc?.Id, acc is null ? null : $"{acc.Code} — {acc.Name}");
        }).ToList();
    }

    /// <summary>Creates whichever of the five standard VAT control accounts are still missing from
    /// this company's Chart of Accounts, then maps every active tax code that actually charges VAT
    /// (see <see cref="ChargingTaxTypes"/>) to the right Output/Input accounts — Reverse Charge
    /// codes get the two Reverse Charge accounts, everything else gets the standard
    /// <see cref="WellKnownAccounts.VatPayable"/>/<see cref="WellKnownAccounts.VatInput"/> pair.
    /// Safe to run repeatedly: never recreates an account that already exists by code, and never
    /// overwrites a code that already has both an Output and an Input account set.</summary>
    public async Task AutoConfigureAsync(string createdBy)
    {
        // Checked here even though _coa.CreateAsync below independently requires CanPost — a
        // CanAdminister caller always satisfies CanPost too (see ICurrentCompany.CanAdminister),
        // so this is the one clean rejection point rather than surfacing CanPost's message instead.
        if (!_current.CanAdminister)
            throw new PostingException("You don't have permission to do this.");

        var byCode = new Dictionary<string, int>();
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var codes = ControlAccounts.Select(c => c.Code).ToList();
            byCode = await db.Accounts.AsNoTracking()
                .Where(a => codes.Contains(a.Code))
                .ToDictionaryAsync(a => a.Code, a => a.Id);
        }

        foreach (var c in ControlAccounts)
        {
            if (byCode.ContainsKey(c.Code)) continue;
            var created = await _coa.CreateAsync(
                new NewAccountInput(c.Code, c.Name, c.Type, IsPostable: true,
                    Category: c.Type == AccountType.Liability ? "Current liability" : "Current asset",
                    Currency: "AED", ParentId: null, Description: c.Description, OpeningBalance: 0),
                createdBy);
            byCode[c.Code] = created.Id;
        }

        var outputAccountId = byCode[WellKnownAccounts.VatPayable];
        var inputAccountId = byCode[WellKnownAccounts.VatInput];
        var rcPayableId = byCode[WellKnownAccounts.ReverseChargeVatPayable];
        var rcRecoverableId = byCode[WellKnownAccounts.ReverseChargeVatRecoverable];

        await using var db2 = await _dbf.CreateDbContextAsync();
        var codesToMap = await db2.TaxCodes
            .Where(t => t.IsActive && (t.OutputAccountId == null || t.InputAccountId == null))
            .ToListAsync();
        foreach (var tax in codesToMap)
        {
            if (!ChargingTaxTypes.Contains(tax.TaxType)) continue;
            if (tax.TaxType == VatTaxType.ReverseCharge)
            {
                tax.OutputAccountId ??= rcPayableId;
                tax.InputAccountId ??= rcRecoverableId;
            }
            else
            {
                tax.OutputAccountId ??= outputAccountId;
                tax.InputAccountId ??= inputAccountId;
            }
        }
        await JournalPoster.SaveChangesTranslatedAsync(db2);
    }
}
