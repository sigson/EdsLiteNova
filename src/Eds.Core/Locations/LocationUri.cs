using System.Text;

namespace Eds.Core.Locations;

/// <summary>
/// A minimal, round-trippable URI value used to identify and persist locations.
/// Replaces the Android <c>android.net.Uri</c> the original used.
///
/// <para>We deliberately do <em>not</em> use <see cref="System.Uri"/> here: a
/// container/EncFS location embeds the <em>whole base-location URI</em> as a query
/// parameter (<c>?location=&lt;encoded uri&gt;</c>), and round-tripping a nested,
/// arbitrarily-escaped URI through <see cref="System.Uri"/>'s scheme-specific
/// canonicalisation is fragile. This type keeps the model explicit — scheme, an
/// unescaped '/'-path, and an ordered query map — and escapes only at the string
/// boundary, so <c>Parse(u.ToString()).Equals(u)</c> always holds.</para>
///
/// <para>Grammar: <c>scheme ':' path ['?' k '=' v ('&amp;' k '=' v)*]</c>.
/// Path segments and query values are percent-encoded with
/// <see cref="Uri.EscapeDataString"/>.</para>
/// </summary>
public sealed class LocationUri : IEquatable<LocationUri>
{
    public string Scheme { get; }

    /// <summary>Unescaped path, always starting with '/'.</summary>
    public string Path { get; }

    private readonly List<KeyValuePair<string, string>> _query;

    public LocationUri(string scheme, string path, IEnumerable<KeyValuePair<string, string>>? query = null)
    {
        if (string.IsNullOrEmpty(scheme)) throw new ArgumentException("Scheme required", nameof(scheme));
        Scheme = scheme;
        Path = NormalizePath(path);
        _query = query == null ? new() : new(query);
    }

    public IReadOnlyList<KeyValuePair<string, string>> Query => _query;

    public string? GetQueryParameter(string key)
    {
        foreach (var kv in _query)
            if (kv.Key == key) return kv.Value;
        return null;
    }

    public LocationUri WithPath(string path) => new(Scheme, path, _query);

    public LocationUri WithQueryParameter(string key, string value)
    {
        var q = new List<KeyValuePair<string, string>>(_query);
        int i = q.FindIndex(kv => kv.Key == key);
        var pair = new KeyValuePair<string, string>(key, value);
        if (i >= 0) q[i] = pair; else q.Add(pair);
        return new LocationUri(Scheme, Path, q);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        return path.StartsWith('/') ? path : "/" + path;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Scheme).Append(':');
        // Escape each path segment but keep the '/' separators readable.
        var segs = Path.Split('/');
        for (int i = 0; i < segs.Length; i++)
        {
            if (i > 0) sb.Append('/');
            sb.Append(Uri.EscapeDataString(segs[i]));
        }
        if (_query.Count > 0)
        {
            sb.Append('?');
            for (int i = 0; i < _query.Count; i++)
            {
                if (i > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(_query[i].Key))
                  .Append('=')
                  .Append(Uri.EscapeDataString(_query[i].Value));
            }
        }
        return sb.ToString();
    }

    public static LocationUri Parse(string s)
    {
        if (string.IsNullOrEmpty(s)) throw new FormatException("Empty URI");
        int colon = s.IndexOf(':');
        if (colon <= 0) throw new FormatException("Missing scheme: " + s);
        string scheme = s[..colon];
        string rest = s[(colon + 1)..];

        string pathPart;
        var query = new List<KeyValuePair<string, string>>();
        int q = rest.IndexOf('?');
        if (q >= 0)
        {
            pathPart = rest[..q];
            string queryPart = rest[(q + 1)..];
            foreach (var pair in queryPart.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0)
                    query.Add(new(Uri.UnescapeDataString(pair), ""));
                else
                    query.Add(new(
                        Uri.UnescapeDataString(pair[..eq]),
                        Uri.UnescapeDataString(pair[(eq + 1)..])));
            }
        }
        else
            pathPart = rest;

        var segs = pathPart.Split('/');
        for (int i = 0; i < segs.Length; i++) segs[i] = Uri.UnescapeDataString(segs[i]);
        return new LocationUri(scheme, string.Join('/', segs), query);
    }

    public static bool TryParse(string s, out LocationUri? uri)
    {
        try { uri = Parse(s); return true; }
        catch { uri = null; return false; }
    }

    public bool Equals(LocationUri? other)
        => other != null && ToString() == other.ToString();

    public override bool Equals(object? obj) => Equals(obj as LocationUri);
    public override int GetHashCode() => ToString().GetHashCode();
}
