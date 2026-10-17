namespace SVNRevDesk.Core.Models;

public sealed record HistoryPage(IReadOnlyList<HistoryEntry> Entries, long PegRevision, long? NextStartRevision);
