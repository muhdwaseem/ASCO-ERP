using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Input line for creating a delivery note from the UI (non-financial — quantity only).</summary>
public record DeliveryLineInput(string Description, string? Unit, decimal Quantity);

public class DeliveryNoteService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;
    public DeliveryNoteService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    public async Task<List<DeliveryNote>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.DeliveryNotes.AsNoTracking()
            .Include(n => n.Customer).Include(n => n.Lines).Include(n => n.SalesInvoice)
            .OrderByDescending(n => n.Date).ThenByDescending(n => n.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>One delivery note with full detail, for the delivery note detail view.</summary>
    public async Task<DeliveryNote?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.DeliveryNotes.AsNoTracking()
            .Include(n => n.Customer).Include(n => n.Lines).Include(n => n.SalesInvoice)
            .FirstOrDefaultAsync(n => n.Id == id);
    }

    /// <summary>Creates a delivery note (non-posting document — records what was dispatched).</summary>
    public async Task<DeliveryNote> CreateAsync(
        int customerId, int? salesInvoiceId, DateOnly date, string? deliveryAddress, string? narration,
        string createdBy, IEnumerable<DeliveryLineInput> lines, DateTime nowUtc)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        if (customerId == 0) throw new PostingException("Delivery note has no customer.");
        var lineList = lines.ToList();
        if (lineList.Count == 0) throw new PostingException("Delivery note needs at least one line.");

        await using var db = await _dbf.CreateDbContextAsync();
        var customer = await db.Customers.FindAsync(customerId)
            ?? throw new PostingException("Customer not found.");

        if (salesInvoiceId is int invId)
        {
            var invoice = await db.SalesInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invId)
                ?? throw new PostingException("Invoice not found.");
            if (invoice.CustomerId != customerId)
                throw new PostingException("Invoice belongs to a different customer.");
        }

        var numbers = await db.DeliveryNotes.Select(n => n.DeliveryNoteNo).ToListAsync();

        var note = new DeliveryNote
        {
            DeliveryNoteNo = JournalPoster.NextDocNo(numbers, "DLV", date.Year),
            CustomerId = customerId,
            SalesInvoiceId = salesInvoiceId,
            Date = date,
            DeliveryAddress = string.IsNullOrWhiteSpace(deliveryAddress) ? null : deliveryAddress.Trim(),
            Status = DocumentStatus.Draft,
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Delivery — {customer.Name}" : narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };

        var no = 1;
        foreach (var l in lineList)
        {
            if (l.Quantity <= 0) throw new PostingException($"Line {no} quantity must be positive.");
            note.Lines.Add(new DeliveryNoteLine
            {
                LineNo = no++,
                Description = l.Description,
                Unit = string.IsNullOrWhiteSpace(l.Unit) ? null : l.Unit.Trim(),
                Quantity = l.Quantity,
            });
        }

        db.DeliveryNotes.Add(note);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return note;
    }

    /// <summary>Marks a delivery note as delivered (or back to draft).</summary>
    public async Task SetStatusAsync(int deliveryNoteId, DocumentStatus status)
    {
        if (!_current.CanPost) throw new PostingException("You don't have permission to do this.");
        await using var db = await _dbf.CreateDbContextAsync();
        var note = await db.DeliveryNotes.FirstOrDefaultAsync(n => n.Id == deliveryNoteId)
            ?? throw new PostingException("Delivery note not found.");
        note.Status = status;
        await db.SaveChangesAsync();
    }
}
