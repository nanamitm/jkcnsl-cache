namespace jkcnsl_cache;

public sealed record JkServiceMapEntry(
    ServiceKey ServiceKey,
    string JkId,
    bool IsPrimary = true,
    string? Notes = null);

public static class NetworkServiceIdTable
{
    // NicoJK の ntsID テーブルをそのまま移植するのではなく、
    // 実 ONID/TSID/SID ベースの ServiceKey を正規化した対応表として持つ。
    // 地上波の実 ONID/TSID は別途確定後に追加する。
    public static readonly IReadOnlyList<JkServiceMapEntry> All = new JkServiceMapEntry[]
    {
    };

    public static readonly IReadOnlyDictionary<ServiceKey, JkServiceMapEntry> ByServiceKey =
        All.GroupBy(entry => entry.ServiceKey)
            .ToDictionary(group => group.Key, group => group.First());

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<JkServiceMapEntry>> ByJkId =
        All.GroupBy(entry => entry.JkId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<JkServiceMapEntry>)group.ToArray(),
                StringComparer.Ordinal);
}
