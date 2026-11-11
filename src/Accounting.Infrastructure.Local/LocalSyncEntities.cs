namespace Accounting.Infrastructure.Local;

public sealed class LocalSyncState
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BranchId { get; set; }
    public Guid DeviceId { get; set; }
    public long LastPulledWatermark { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public string EnabledModulesCsv { get; set; } = "RETAIL";
    public DateTimeOffset? LastSuccessfulSyncUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class LocalSyncConflict
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BranchId { get; set; }
    public Guid OperationId { get; set; }
    public Guid DocumentId { get; set; }
    public string ConflictCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string ServerPayloadJson { get; set; } = string.Empty;
    public DateTimeOffset ServerTimestampUtc { get; set; }
    public bool IsResolved { get; set; }
}

public sealed class LocalSequenceCounter
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BranchId { get; set; }
    public Guid DeviceId { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public long CurrentValue { get; set; }
}

public sealed class LocalModuleConfiguration
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BranchId { get; set; }
    public string ModuleKey { get; set; } = string.Empty;
    public string ConfigurationJson { get; set; } = "{}";
}


public sealed class LocalCompanyBrandingSettings
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BranchId { get; set; }
    public string ArabicName { get; set; } = string.Empty;
    public string EnglishName { get; set; } = string.Empty;
    public string BusinessDescription { get; set; } = string.Empty;
    public byte[]? LogoBytes { get; set; }
    public string? LogoFileName { get; set; }
    public string? LogoMimeType { get; set; }
    public string City { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Phones { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string TaxNumber { get; set; } = string.Empty;
    public string CommercialRegistration { get; set; } = string.Empty;
    public string FooterNotes { get; set; } = string.Empty;
    public string CurrencyCode { get; set; } = "YER";
    public string CurrencySymbol { get; set; } = "YER";
    public bool TaxEnabled { get; set; }
    public decimal TaxRate { get; set; }
    public bool AllowNegativeStock { get; set; }
    public bool DarkThemeEnabled { get; set; }
    public long UpdatedAtUtcTicks { get; set; }
}
