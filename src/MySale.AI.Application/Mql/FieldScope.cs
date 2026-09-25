using MySale.AI.Application.Abstractions;

namespace MySale.AI.Application.Mql;

/// <summary>
/// The set of field names visible at a point in a pipeline. Starts from the collection schema and is
/// reshaped by $group / $project / $addFields / $lookup / $unwind / $count / $facet.
/// A null sub-scope means "any sub-path allowed" (e.g. computed objects).
/// </summary>
internal sealed class FieldScope
{
    private readonly Dictionary<string, FieldScope?> _roots = new(StringComparer.Ordinal);

    /// <summary>When true, any field is accepted (collection with no schema metadata).</summary>
    public bool Open { get; private set; }

    public IEnumerable<string> Roots => _roots.Keys;

    public static FieldScope FromSchema(CollectionSchema? schema, string tenantField)
    {
        var scope = new FieldScope();
        scope.Add("_id");
        if (schema is null || schema.Fields.Count == 0)
        {
            scope.Open = true;
            return scope;
        }
        foreach (var f in schema.Fields)
        {
            if (string.Equals(f.Name, tenantField, StringComparison.OrdinalIgnoreCase)) continue;
            var root = f.Name.Split('.')[0];
            if (f.Name.Contains('.'))
            {
                scope._roots[root] = null; // nested documented path: allow sub-paths
            }
            else if (!scope._roots.ContainsKey(root))
            {
                scope.Add(root);
            }
        }
        return scope;
    }

    public static FieldScope Empty() => new();

    public FieldScope Clone()
    {
        var c = new FieldScope { Open = Open };
        foreach (var kv in _roots) c._roots[kv.Key] = kv.Value?.Clone();
        return c;
    }

    public void Add(string root, FieldScope? sub = null) => _roots[root] = sub;

    public void Remove(string root) => _roots.Remove(root);

    public bool TryGetSub(string root, out FieldScope? sub) => _roots.TryGetValue(root, out sub);

    public bool Contains(string path)
    {
        if (Open) return true;
        var dot = path.IndexOf('.');
        var root = dot < 0 ? path : path[..dot];
        if (!_roots.TryGetValue(root, out var sub)) return false;
        if (dot < 0 || sub is null) return true;
        return sub.Contains(path[(dot + 1)..]);
    }

    public string? Suggest(string path)
    {
        var root = path.Split('.')[0];
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (var candidate in _roots.Keys)
        {
            if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return candidate;
            var d = Levenshtein(candidate.ToLowerInvariant(), root.ToLowerInvariant());
            if (d < bestDistance) { bestDistance = d; best = candidate; }
        }
        return bestDistance <= 2 ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
