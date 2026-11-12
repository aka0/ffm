namespace FFM.App.Services;

public sealed record NewgenPlayer(string Id, int? Ethnicity, int? SkinTone, string? Nationality,
    string? FirstName = null, string? LastName = null, int? Age = null, DateOnly? BirthDate = null, int? NationId = null, string? Club = null)
{
    public bool HasAppearance => Ethnicity.HasValue && SkinTone.HasValue;
    public string Name => string.Join(" ", new[] { FirstName, LastName }.Where(n => !string.IsNullOrWhiteSpace(n)));
}
public sealed record PortraitFile(string Category, string RelativePath, string FileStem);
public sealed record PortraitMapping(string PlayerId, string Category, string RelativePath);
public sealed record ConfigEntry(string PlayerId, string Category, string FromPath);
public sealed record ScanProgress(double Percent, string Detail, bool IsIndeterminate = false);
public sealed record MappingProgress(double Percent, string Detail, bool IsIndeterminate = false);
public sealed record SaveScanResult(IReadOnlyList<NewgenPlayer> Players, IReadOnlyList<string> AllPlayerIds, string Message, IReadOnlyList<PlayerRecordIdentity> Identities);
public sealed record PlayerRecordIdentity(string Id, byte ObjectKind, string? FirstName = null, string? LastName = null, int? Age = null, DateOnly? BirthDate = null, int? NationId = null);
public sealed record PlayerRecordScanOutcome(
    IReadOnlyList<NewgenPlayer> Players,
    int ValidatedPlayerRecords,
    int RecordsWithClosingUniqueId,
    int NewgenIdsByPrefix,
    IReadOnlyList<string> AllPlayerIds,
    uint? MinimumUniqueId,
    uint? MaximumUniqueId,
    IReadOnlyList<PlayerRecordIdentity> Identities,
    int UndecodableNames = 0,
    int AmbiguousCandidates = 0);
