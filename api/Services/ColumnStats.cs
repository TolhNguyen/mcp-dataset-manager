namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Mutable per-column accumulator used while streaming rows.
/// Tracks null counts, distinct counts (capped), sample values, and per-type counters.
/// </summary>
internal sealed class ColumnStats
{
    private const int MaxDistinct = 10_000;
    private const int MaxSamples = 5;

    private readonly HashSet<string> _distinct = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _samples = new(MaxSamples);
    private long _total;
    private long _nulls;
    private long _numberLike;
    private long _dateLike;
    private long _booleanLike;
    private long _stringLike;
    private bool _distinctCapped;

    public string? OriginalHeader { get; }
    public string NormalizedName { get; }

    public ColumnStats(string? originalHeader, string normalizedName)
    {
        OriginalHeader = originalHeader;
        NormalizedName = normalizedName;
    }

    public void Add(string? value)
    {
        _total++;

        if (string.IsNullOrWhiteSpace(value))
        {
            _nulls++;
            return;
        }

        var trimmed = value.Trim();

        if (_samples.Count < MaxSamples && !_samples.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            _samples.Add(trimmed);
        }

        if (!_distinctCapped)
        {
            _distinct.Add(trimmed);
            if (_distinct.Count >= MaxDistinct) _distinctCapped = true;
        }

        var isNumber = TypeInferrer.LooksLikeNumber(trimmed);
        var isDate = TypeInferrer.LooksLikeDate(trimmed);
        var isBool = TypeInferrer.LooksLikeBoolean(trimmed);

        if (isNumber) _numberLike++;
        if (isDate) _dateLike++;
        if (isBool) _booleanLike++;
        if (!isNumber && !isDate && !isBool) _stringLike++;
    }

    public long Total => _total;
    public long Nulls => _nulls;
    public long DistinctCount => _distinct.Count;
    public bool DistinctCapped => _distinctCapped;
    public IReadOnlyList<string> Samples => _samples;

    public string ReduceType()
    {
        var nonNull = _total - _nulls;
        return TypeInferrer.Reduce(nonNull, _numberLike, _dateLike, _booleanLike, _stringLike);
    }

    public string? GuessSemanticType()
    {
        var name = NormalizedName;
        if (name.Contains("doanh_thu") || name.Contains("revenue") || name.Contains("doanh_so")) return "revenue";
        if (name.Contains("ngay") || name.Contains("date") || name.EndsWith("_at")) return "date";
        if (name.Contains("email")) return "email";
        if (name.Contains("sdt") || name.Contains("phone") || name.Contains("dien_thoai")) return "phone";
        if (name.StartsWith("ma_") || name.EndsWith("_id") || name == "id") return "identifier";
        if (name.Contains("so_luong") || name == "qty" || name == "quantity") return "quantity";
        if (name.Contains("gia_") || name.Contains("price") || name.Contains("don_gia")) return "price";
        if (name.Contains("ten_") || name.Contains("_name") || name == "name") return "name";
        return null;
    }

    public string[] BuildAliases()
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizedName,
            NormalizedName.Replace('_', ' ')
        };

        if (!string.IsNullOrWhiteSpace(OriginalHeader))
        {
            aliases.Add(OriginalHeader.Trim());
            // Add a no-accent lowercase variant of the original header for additional matching.
            aliases.Add(OriginalHeader.Trim().ToLowerInvariant());
        }

        // Remove the normalized name itself if the user prefers original aliases displayed separately.
        return aliases.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
    }
}
