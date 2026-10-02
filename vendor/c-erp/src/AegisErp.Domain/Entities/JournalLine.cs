namespace AegisErp.Domain.Entities;

/// <summary>A single debit or credit line within a voucher. Exactly one of Debit/Credit is non-zero.</summary>
public class JournalLine : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>
    /// Owning company, denormalised from the voucher. Reports query <c>JournalLines</c> directly,
    /// so the line needs its own filter rather than relying on the voucher's.
    /// </summary>
    public int CompanyId { get; set; }

    public int JournalVoucherId { get; set; }
    public JournalVoucher JournalVoucher { get; set; } = null!;

    public int LineNo { get; set; }

    public int AccountId { get; set; }
    public Account Account { get; set; } = null!;

    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public string? Description { get; set; }

    public decimal Debit { get; set; }
    public decimal Credit { get; set; }

    /// <summary>Signed movement: positive = debit, negative = credit.</summary>
    public decimal Signed => Debit - Credit;
}
