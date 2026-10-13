using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WhatsAHealthBar;

/// <summary>
/// A data text template ("{name}", "{hp%}%  {ttk}"), parsed once into literal and token segments (idea and token set from
/// syrairc's ExileNameplates). A bar's string is rebuilt only when the values its tokens read change: <see cref="Signature"/>
/// hashes just those values (no allocation), and the bar keeps the last string per slot.
/// Tokens: {name} {hp} {hpmax} {hp%} (life + ES + ward) {life} {lifemax} {life%} {es} {esmax} {es%} {ward} {wardmax} {ward%}
/// {dist} {rarity} {dps} {ttk} {dotloss} {dotdps}. Unknown tokens stay as written, so a typo shows on the bar.
/// </summary>
public sealed class TextTemplate
{
    public enum Tok : byte
    {
        Lit, Name, Hp, HpMax, HpPct, Life, LifeMax, LifePct, Es, EsMax, EsPct, Ward, WardMax, WardPct,
        Dist, Rarity, Dps, Ttk, DotLoss, DotDps,
    }

    public static readonly string[] TokenNames =
        { "name", "hp", "hpmax", "hp%", "life", "lifemax", "life%", "es", "esmax", "es%", "ward", "wardmax", "ward%", "dist", "rarity", "dps", "ttk", "dotloss", "dotdps" };

    private static readonly Dictionary<string, Tok> Tokens = BuildTokens();
    private static readonly StringBuilder Sb = new(64);

    private readonly Tok[] _toks;
    private readonly string?[] _lits;

    public readonly string Source;
    /// <summary>No text at all: the slot draws nothing.</summary>
    public bool IsEmpty => _toks.Length == 0;
    /// <summary>The whole template is literal text (no tokens): the string never changes.</summary>
    public readonly string? Constant;

    public static readonly TextTemplate Empty = new("", Array.Empty<Tok>(), Array.Empty<string?>());

    private TextTemplate(string source, Tok[] toks, string?[] lits)
    {
        Source = source;
        _toks = toks;
        _lits = lits;
        if (toks.Length == 1 && toks[0] == Tok.Lit) Constant = lits[0];
    }

    public static TextTemplate Parse(string? template)
    {
        if (string.IsNullOrEmpty(template)) return Empty;
        var toks = new List<Tok>();
        var lits = new List<string?>();
        int i = 0, lit = 0;
        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                var end = template.IndexOf('}', i + 1);
                if (end > i && Tokens.TryGetValue(template.Substring(i + 1, end - i - 1), out var tok))
                {
                    if (i > lit) { toks.Add(Tok.Lit); lits.Add(template[lit..i]); }
                    toks.Add(tok); lits.Add(null);
                    i = lit = end + 1;
                    continue;
                }
            }
            i++;
        }
        if (lit < template.Length) { toks.Add(Tok.Lit); lits.Add(template[lit..]); }
        return new TextTemplate(template, toks.ToArray(), lits.ToArray());
    }

    /// <summary>A hash of the values this template reads from the bar: equal signatures give equal strings.</summary>
    public long Signature(Bar b)
    {
        long h = 17;
        foreach (var t in _toks)
            if (t != Tok.Lit) h = h * 31 + Value(t, b);
        return h;
    }

    /// <summary>The integer each token shows (or a stand-in that changes exactly when its text does).</summary>
    private static long Value(Tok t, Bar b) => t switch
    {
        Tok.Name => b.NameVersion,
        Tok.Hp => b.Total,
        Tok.HpMax => b.BaseTotal,
        Tok.HpPct => Pct(b.Total, b.BaseTotal),
        Tok.Life => b.Cur[Bar.Life],
        Tok.LifeMax => b.BaseMax[Bar.Life],
        Tok.LifePct => Pct(b.Cur[Bar.Life], b.BaseMax[Bar.Life]),
        Tok.Es => b.Cur[Bar.Es],
        Tok.EsMax => b.BaseMax[Bar.Es],
        Tok.EsPct => Pct(b.Cur[Bar.Es], b.BaseMax[Bar.Es]),
        Tok.Ward => b.Cur[Bar.Ward],
        Tok.WardMax => b.BaseMax[Bar.Ward],
        Tok.WardPct => Pct(b.Cur[Bar.Ward], b.BaseMax[Bar.Ward]),
        Tok.Dist => (int)b.Dist,
        Tok.Rarity => (int)b.Kind,
        Tok.Dps => Shown(b.Dps),
        Tok.Ttk => b.Ttk < 0 ? -1 : (long)(b.Ttk * 10),
        Tok.DotLoss => Shown(b.DotLossShown),
        Tok.DotDps => Shown(b.DotRate),
        _ => 0,
    };

    /// <summary>Three significant digits: what <see cref="Abbrev"/> can show, so a tiny drift doesn't rebuild the string.</summary>
    private static long Shown(float v) => v < 1000 ? (long)v : v < 1_000_000 ? (long)(v / 100) * 100 : (long)(v / 100_000) * 100_000;

    public string Build(Bar b)
    {
        if (Constant != null) return Constant;
        Sb.Clear();
        for (var i = 0; i < _toks.Length; i++)
        {
            var t = _toks[i];
            switch (t)
            {
                case Tok.Lit: Sb.Append(_lits[i]); break;
                case Tok.Name: Sb.Append(b.DisplayName); break;
                case Tok.Rarity: Sb.Append(BarRenderer.KindNames[(int)b.Kind]); break;
                case Tok.Dist: Sb.Append((int)b.Dist); break;
                case Tok.HpPct: case Tok.LifePct: case Tok.EsPct: case Tok.WardPct: Sb.Append(Value(t, b)); break;
                case Tok.Ttk: AppendTtk(b.Ttk); break;
                case Tok.Dps: Abbrev(b.Dps); break;
                case Tok.DotLoss: Abbrev(b.DotLossShown); break;
                case Tok.DotDps: Abbrev(b.DotRate); break;
                default: Abbrev(Value(t, b)); break;
            }
        }
        return Sb.ToString();
    }

    private static int Pct(int cur, int max) => max <= 0 ? 0 : Math.Clamp((int)MathF.Round(cur * 100f / max), 0, 999);

    private static void AppendTtk(float s)
    {
        if (s < 0) { Sb.Append('-'); return; }
        if (s < 10) Sb.Append(s.ToString("0.0", CultureInfo.InvariantCulture)).Append('s');
        else if (s < 120) Sb.Append((int)s).Append('s');
        else Sb.Append((int)(s / 60)).Append('m');
    }

    /// <summary>12345 -> "12.3k", 2500000 -> "2.5m".</summary>
    private static void Abbrev(float v)
    {
        if (v < 0) v = 0;
        if (v >= 1_000_000) Sb.Append(Trim(v / 1_000_000f)).Append('m');
        else if (v >= 10_000) Sb.Append(Trim(v / 1000f)).Append('k');
        else Sb.Append((int)v);
    }

    private static string Trim(float x) => x >= 100 ? ((int)x).ToString(CultureInfo.InvariantCulture) : x.ToString("0.#", CultureInfo.InvariantCulture);

    private static Dictionary<string, Tok> BuildTokens()
    {
        var d = new Dictionary<string, Tok>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < TokenNames.Length; i++) d[TokenNames[i]] = (Tok)(i + 1);
        return d;
    }

    /// <summary>"Skeleton Soldier Captain" -> "S. S. Captain" when longer than <paramref name="threshold"/> (0: never).</summary>
    public static string Abbreviate(string name, int threshold)
    {
        if (threshold <= 0 || name.Length <= threshold) return name;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1) return name;
        var sb = new StringBuilder(name.Length);
        for (var i = 0; i < words.Length - 1; i++) sb.Append(char.ToUpperInvariant(words[i][0])).Append(". ");
        return sb.Append(words[^1]).ToString();
    }
}
