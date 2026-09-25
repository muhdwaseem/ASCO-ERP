namespace Asco.Api.Modules;

// ASCO-owned tables (prefix asco_) for industry modules C-ERP doesn't have. They live in the same
// database as C-ERP's tables but C-ERP's code and migrations never touch them. Every row carries
// CompanyId and is filtered by the same CurrentCompany that scopes C-ERP's own queries.

public interface IModuleScoped { int CompanyId { get; set; } }

public enum Industry { General = 1, Trading = 2, Retail = 3, Manufacturing = 4, Logistics = 5, Construction = 6, Services = 7 }

public class CompanyProfile : IModuleScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Industry Industry { get; set; } = Industry.General;
    /// <summary>Comma-separated module keys: inventory, manufacturing, jobs, fleet.</summary>
    public string Modules { get; set; } = "";
    // GL mapping (C-ERP account ids) used by module postings.
    public int? InventoryAccountId { get; set; }
    public int? CogsAccountId { get; set; }
    public int? StockClearingAccountId { get; set; }
    public int? StockAdjustmentAccountId { get; set; }
    public int? ConversionCostAccountId { get; set; }
    public int? FleetExpenseAccountId { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
}

// ── Inventory ────────────────────────────────────────────────────────────────
public class Warehouse : IModuleScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class StockItemSetting : IModuleScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int ItemId { get; set; }            // C-ERP Item.Id
    public decimal ReorderLevel { get; set; }
}

public enum StockMoveType { Receipt = 1, Issue = 2, Transfer = 3, Adjustment = 4, SaleIssue = 5, ProductionIssue = 6, ProductionReceipt = 7 }

public class StockMove : IModuleScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string MoveNo { get; set; } = "";
    public StockMoveType Type { get; set; }
    public DateOnly Date { get; set; }
    public string? Reference { get; set; }
    public string? Narration { get; set; }
    public string? VoucherNo { get; set; }     // C-ERP GL voucher posted for this move, if any
    public int? ProductionOrderId { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public List<StockMoveLine> Lines { get; set; } = new();
}

public class StockMoveLine : IModuleScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int StockMoveId { get; set; }
    public StockMove StockMove { get; set; } = null!;
    public int ItemId { get; set; }
    public int WarehouseId { get; set; }
    /// <summary>Signed: positive = into the warehouse, negative = out.</summary>
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    /// <summary>Signed value (Quantity × UnitCost, rounded) — the inventory valuation movement.</summary>
    public decimal Value { get; set; }
}

// ── Manufacturing ────────────────────────────────────────────────────────────
public class Bom : IModuleScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int OutputItemId { get; set; }
    public decimal OutputQuantity { get; set; } = 1;
    /// <summary>Labour + overhead absorbed per batch of OutputQuantity.</summary>
    public decimal ConversionCost { get; set; }
    public bool IsActive { get; set; } = true;
    public List<BomLine> Lines { get; set; } = new();
}

public class BomLine : IModuleScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int BomId { get; set; }
    public Bom Bom { get; set; } = null!;
    public int ComponentItemId { get; set; }
    public decimal Quantity { get; set; }
}

public enum ProductionStatus { Released = 1, Completed = 2, Cancelled = 3 }

public class ProductionOrder : IModuleScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string OrderNo { get; set; } = "";
    public int BomId { get; set; }
    public Bom Bom { get; set; } = null!;
    public decimal Quantity { get; set; }
    public int SourceWarehouseId { get; set; }
    public int OutputWarehouseId { get; set; }
    public DateOnly PlannedDate { get; set; }
    public ProductionStatus Status { get; set; } = ProductionStatus.Released;
    public DateOnly? CompletedDate { get; set; }
    public decimal MaterialCost { get; set; }
    public decimal ConversionCost { get; set; }
    public decimal UnitCost { get; set; }
    public string? VoucherNo { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

// ── Jobs (logistics shipments, construction projects, service jobs) + fleet ──
public enum JobType { Shipment = 1, Project = 2, Service = 3 }
public enum JobStatus { Open = 1, InProgress = 2, Completed = 3, Closed = 4, Cancelled = 5 }
public enum TransportMode { Air = 1, Sea = 2, Road = 3, Courier = 4, Rail = 5 }

public class Job : IModuleScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string JobNo { get; set; } = "";
    public string Title { get; set; } = "";
    public JobType Type { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Open;
    public int? CustomerId { get; set; }        // C-ERP Customer.Id
    public int? CostCenterId { get; set; }      // C-ERP CostCenter that tags this job's GL lines
    public decimal Budget { get; set; }
    public DateOnly OpenedDate { get; set; }
    public DateOnly? ClosedDate { get; set; }
    // Shipment details (logistics)
    public TransportMode? Mode { get; set; }
    public string? Direction { get; set; }       // Import / Export / Domestic
    public string? Origin { get; set; }
    public string? Destination { get; set; }
    public string? Carrier { get; set; }
    public string? AwbBl { get; set; }           // air waybill / bill of lading
    public string? ContainerNo { get; set; }
    public int? Packages { get; set; }
    public decimal? WeightKg { get; set; }
    public DateOnly? Etd { get; set; }
    public DateOnly? Eta { get; set; }
    // Project details (construction / services)
    public string? SiteLocation { get; set; }
    public DateOnly? TargetDate { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public class Vehicle : IModuleScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string PlateNo { get; set; } = "";
    public string Type { get; set; } = "";       // Truck / Van / Pickup / Trailer…
    public decimal? CapacityKg { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Trip : IModuleScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string TripNo { get; set; } = "";
    public int VehicleId { get; set; }
    public Vehicle Vehicle { get; set; } = null!;
    public int? JobId { get; set; }
    public string? Driver { get; set; }
    public DateOnly Date { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public decimal DistanceKm { get; set; }
    public decimal FuelLitres { get; set; }
    public decimal FuelCost { get; set; }
    public decimal Tolls { get; set; }
    public decimal OtherCost { get; set; }
    public string? VoucherNo { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

// ── AI audit log (Phase 4) ───────────────────────────────────────────────────
public class AiLog : IModuleScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string User { get; set; } = "";
    public string Question { get; set; } = "";
    public string? Answer { get; set; }
    public string? ToolsUsed { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public bool Succeeded { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
