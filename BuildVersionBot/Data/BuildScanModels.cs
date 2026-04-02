namespace BuildVersionBot.Data;

public record BuildScanTarget(string ComputerName);

public record BuildScanUpdate(
    string ComputerName,
    DateTime ScanTime,
    string OperatorName,
    string ResultValue,
    bool MarkDone
);

public record FailedItem(string ComputerName, string Error);

public record BatchUpdateResult(
    int Updated,
    int SkippedBecauseChanged,
    int Failed,
    IReadOnlyList<BuildScanUpdate> UpdatedItems,
    IReadOnlyList<string> SkippedItems,
    IReadOnlyList<FailedItem> FailedItems
);
