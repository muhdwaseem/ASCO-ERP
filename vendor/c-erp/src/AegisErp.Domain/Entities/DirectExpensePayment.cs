namespace AegisErp.Domain.Entities;

/// <summary>
/// A payoff of a "Pay Later" <see cref="Entities.DirectExpense"/> (money out). Posting generates a
/// GL voucher (Dr Expenses Payable / Cr bank). Always targets exactly one Direct Expense — unlike
/// <see cref="VendorPayment"/>, this never spans multiple documents — but can still split its
/// amount across that expense's own lines via <see cref="Allocations"/>. Posts immediately: there
/// is no Draft stage.
/// </summary>
public class DirectExpensePayment : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Document number, e.g. "EXP-PMT-2026-0001". Shared with the generated GL voucher. Unique within the company.</summary>
    public string PaymentNo { get; set; } = string.Empty;

    public int DirectExpenseId { get; set; }
    public DirectExpense DirectExpense { get; set; } = null!;

    public DateOnly Date { get; set; }

    public int FiscalPeriodId { get; set; }
    public FiscalPeriod FiscalPeriod { get; set; } = null!;

    /// <summary>Bank/cash account the money went out of.</summary>
    public int BankAccountId { get; set; }
    public Account BankAccount { get; set; } = null!;

    public decimal Amount { get; set; }
    public PaymentMode PaymentMode { get; set; } = PaymentMode.Cash;

    /// <summary>Cheque or other payment reference number, if any.</summary>
    public string? ReferenceNo { get; set; }
    /// <summary>Cheque date, relevant for Cheque/PostDatedCheque modes.</summary>
    public DateOnly? ChequeDate { get; set; }

    public string? Narration { get; set; }

    public int? JournalVoucherId { get; set; }
    public JournalVoucher? JournalVoucher { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>How this payment's amount is split across the expense's individual lines.</summary>
    public List<DirectExpensePaymentAllocation> Allocations { get; set; } = new();
}

/// <summary>
/// Records how much of a <see cref="DirectExpensePayment"/> was applied against one specific
/// <see cref="DirectExpenseLine"/> — so, on a multi-line "Pay Later" expense, staff can see exactly
/// which charge was paid instead of only a single lump-sum balance for the whole expense.
/// </summary>
public class DirectExpensePaymentAllocation
{
    public int Id { get; set; }

    public int DirectExpensePaymentId { get; set; }
    public DirectExpensePayment DirectExpensePayment { get; set; } = null!;

    public int DirectExpenseLineId { get; set; }
    public DirectExpenseLine DirectExpenseLine { get; set; } = null!;

    public decimal Amount { get; set; }
}
