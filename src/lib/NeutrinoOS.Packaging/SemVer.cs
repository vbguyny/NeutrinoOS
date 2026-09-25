// NeutrinoOS Phase 8 - shared packaging library (NeutrinoOS.Packaging).
//
// SemVer: semantic version parsing/comparison and version constraints, used
// by the npkg dependency resolver (manifest dependencies, repository index)
// on the device and on the host.  Everything is implemented by hand because
// the korlib subset has no Version/CultureInfo APIs and the code must run
// identically under the Tier-0 JIT and desktop .NET.

using System;
using System.Collections.Generic;
using System.Text;

namespace NeutrinoOS.Packaging
{
    /// <summary>
    /// Phase 8: a semantic version (major.minor.patch with an optional
    /// pre-release suffix). Used for package manifests, dependency
    /// constraints and repository indexes. Comparison follows semver:
    /// numeric fields first, then "no pre-release beats a pre-release",
    /// then identifier-by-identifier pre-release comparison.
    /// </summary>
    public struct SemVersion : IEquatable<SemVersion>
    {
        /// <summary>Phase 8: major version component (breaking changes).</summary>
        public int Major;

        /// <summary>Phase 8: minor version component (backwards-compatible additions).</summary>
        public int Minor;

        /// <summary>Phase 8: patch version component (backwards-compatible fixes).</summary>
        public int Patch;

        /// <summary>Phase 8: pre-release suffix such as "rc.1"; null when this is a release version.</summary>
        public string PreRelease;

        /// <summary>
        /// Phase 8: compares two versions per the semver precedence rules.
        /// Returns a negative value, zero or a positive value.
        /// </summary>
        public int CompareTo(SemVersion other)
        {
            if (Major != other.Major)
                return Major < other.Major ? -1 : 1;
            if (Minor != other.Minor)
                return Minor < other.Minor ? -1 : 1;
            if (Patch != other.Patch)
                return Patch < other.Patch ? -1 : 1;

            bool releaseA = PreRelease == null || PreRelease.Length == 0;
            bool releaseB = other.PreRelease == null || other.PreRelease.Length == 0;
            if (releaseA && releaseB)
                return 0;
            if (releaseA)
                return 1;   // release > pre-release
            if (releaseB)
                return -1;
            return ComparePrerelease(PreRelease, other.PreRelease);
        }

        /// <summary>
        /// Phase 8: field-wise equality including the pre-release suffix.
        /// Used by the "=" version constraint and by tests.
        /// </summary>
        public bool Equals(SemVersion other)
        {
            return Major == other.Major && Minor == other.Minor && Patch == other.Patch
                && SamePrerelease(PreRelease, other.PreRelease);
        }

        /// <summary>
        /// Phase 8: canonical text form "1.2.3" or "1.2.3-rc.1" used in
        /// manifests and repository indexes.
        /// </summary>
        public override string ToString()
        {
            string text = TextConv.LongToString((long)Major) + "."
                + TextConv.LongToString((long)Minor) + "."
                + TextConv.LongToString((long)Patch);
            if (PreRelease != null && PreRelease.Length > 0)
                text = text + "-" + PreRelease;
            return text;
        }

        /// <summary>
        /// Phase 8: strict semver parse ("major.minor.patch[-pre][+build]",
        /// exactly three numeric components). Returns false for anything
        /// else; no exception is thrown.
        /// </summary>
        public static bool TryParse(string text, out SemVersion version)
        {
            int components;
            if (!TryParsePartial(text, out version, out components))
                return false;
            return components == 3;
        }

        /// <summary>
        /// Phase 8: strict semver parse; throws FormatException with the
        /// offending text when the input is not a complete version.
        /// </summary>
        public static SemVersion Parse(string text)
        {
            SemVersion version;
            if (!TryParse(text, out version))
                throw new FormatException("invalid semantic version: " + (text == null ? "(null)" : text));
            return version;
        }

        /// <summary>
        /// Phase 8 internal helper: parses 1-3 numeric components plus an
        /// optional pre-release/build suffix. Reports how many numeric
        /// components were present so VersionConstraint can implement the
        /// "~1.2" and "^1.2" shorthand forms.
        /// </summary>
        internal static bool TryParsePartial(string text, out SemVersion version, out int components)
        {
            version = new SemVersion();
            components = 0;
            if (text == null || text.Length == 0)
                return false;

            int i = 0;
            int major;
            int minor = 0;
            int patch = 0;

            if (!ReadNumber(text, ref i, out major))
                return false;
            components = 1;

            if (i < text.Length && text[i] == '.')
            {
                i++;
                if (!ReadNumber(text, ref i, out minor))
                    return false;
                components = 2;

                if (i < text.Length && text[i] == '.')
                {
                    i++;
                    if (!ReadNumber(text, ref i, out patch))
                        return false;
                    components = 3;
                }
            }

            string preRelease = null;
            if (i < text.Length && text[i] == '-')
            {
                i++;
                int start = i;
                while (i < text.Length && IsPrereleaseChar(text[i]))
                    i++;
                if (i == start)
                    return false;
                preRelease = text.Substring(start, i - start);
                if (!ValidateIdentifiers(preRelease))
                    return false;
            }

            if (i < text.Length && text[i] == '+')
            {
                i++;
                int start = i;
                while (i < text.Length && IsPrereleaseChar(text[i]))
                    i++;
                if (i == start)
                    return false;
                if (!ValidateIdentifiers(text.Substring(start, i - start)))
                    return false;
            }

            if (i != text.Length)
                return false;

            version.Major = major;
            version.Minor = minor;
            version.Patch = patch;
            version.PreRelease = preRelease;
            return true;
        }

        private static bool ReadNumber(string text, ref int i, out int value)
        {
            value = 0;
            int start = i;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9')
            {
                int digit = text[i] - '0';
                if (value > 200000000)
                    return false; // far beyond any plausible version; avoids overflow
                value = value * 10 + digit;
                i++;
            }
            return i > start;
        }

        private static bool IsPrereleaseChar(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-' || c == '.';
        }

        private static bool ValidateIdentifiers(string text)
        {
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == '.')
                {
                    if (i == start)
                        return false; // empty identifier
                    start = i + 1;
                }
            }
            return true;
        }

        private static bool SamePrerelease(string a, string b)
        {
            bool emptyA = a == null || a.Length == 0;
            bool emptyB = b == null || b.Length == 0;
            if (emptyA && emptyB)
                return true;
            if (emptyA || emptyB)
                return false;
            return CompareOrdinal(a, b) == 0;
        }

        private static int ComparePrerelease(string a, string b)
        {
            List<string> pa = SplitIdentifiers(a);
            List<string> pb = SplitIdentifiers(b);
            int n = pa.Count < pb.Count ? pa.Count : pb.Count;
            for (int i = 0; i < n; i++)
            {
                int c = CompareIdentifier(pa[i], pb[i]);
                if (c != 0)
                    return c;
            }
            if (pa.Count == pb.Count)
                return 0;
            return pa.Count < pb.Count ? -1 : 1;
        }

        private static List<string> SplitIdentifiers(string text)
        {
            List<string> parts = new List<string>();
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == '.')
                {
                    parts.Add(text.Substring(start, i - start));
                    start = i + 1;
                }
            }
            return parts;
        }

        private static int CompareIdentifier(string a, string b)
        {
            bool numericA = IsNumericIdentifier(a);
            bool numericB = IsNumericIdentifier(b);

            if (numericA && numericB)
            {
                string ta = StripLeadingZeros(a);
                string tb = StripLeadingZeros(b);
                if (ta.Length != tb.Length)
                    return ta.Length < tb.Length ? -1 : 1;
                return CompareOrdinal(ta, tb);
            }
            if (numericA)
                return -1; // numeric identifiers sort before alphanumeric ones
            if (numericB)
                return 1;
            return CompareOrdinal(a, b);
        }

        private static bool IsNumericIdentifier(string text)
        {
            if (text.Length == 0)
                return false;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9')
                    return false;
            }
            return true;
        }

        private static string StripLeadingZeros(string text)
        {
            int i = 0;
            while (i < text.Length - 1 && text[i] == '0')
                i++;
            return text.Substring(i);
        }

        private static int CompareOrdinal(string a, string b)
        {
            int n = a.Length < b.Length ? a.Length : b.Length;
            for (int i = 0; i < n; i++)
            {
                if (a[i] != b[i])
                    return a[i] < b[i] ? -1 : 1;
            }
            if (a.Length == b.Length)
                return 0;
            return a.Length < b.Length ? -1 : 1;
        }
    }

    /// <summary>
    /// Phase 8: a version constraint as used by manifest dependencies and by
    /// "npkg install"/"upgrade". One or more simple constraints (AND) such
    /// as "=1.2.3", "&gt;=1.0.0", "&gt;=1.0.0 &lt;2.0.0", "~1.2.3" or "^1.2.3".
    /// Pre-release simplification: a pre-release version matches "=" exactly
    /// and the ordered comparisons (&gt;, &gt;=, &lt;, &lt;=) by normal precedence,
    /// but never matches the ~/^ ranges (range bounds ignore pre-releases).
    /// </summary>
    public sealed class VersionConstraint
    {
        private const int KindEqual = 0;
        private const int KindGreater = 1;
        private const int KindGreaterEqual = 2;
        private const int KindLess = 3;
        private const int KindLessEqual = 4;
        private const int KindTilde = 5;
        private const int KindCaret = 6;

        private sealed class Simple
        {
            public int Kind;
            public SemVersion Low;
            public SemVersion High;
            public string Text;
        }

        private readonly List<Simple> _parts = new List<Simple>();

        private VersionConstraint()
        {
        }

        /// <summary>
        /// Phase 8: parses a constraint list (whitespace and/or commas
        /// separate the simple constraints, all of which must match).
        /// Returns false without throwing for malformed input.
        /// </summary>
        public static bool TryParse(string text, out VersionConstraint constraint)
        {
            constraint = null;
            if (text == null)
                return false;

            List<Simple> parts = new List<Simple>();
            int i = 0;
            while (true)
            {
                SkipSeparators(text, ref i);
                if (i >= text.Length)
                    break;

                int kind = KindEqual;
                if (Match(text, ref i, "=="))
                    kind = KindEqual;
                else if (Match(text, ref i, ">="))
                    kind = KindGreaterEqual;
                else if (Match(text, ref i, "<="))
                    kind = KindLessEqual;
                else if (Match(text, ref i, ">"))
                    kind = KindGreater;
                else if (Match(text, ref i, "<"))
                    kind = KindLess;
                else if (Match(text, ref i, "="))
                    kind = KindEqual;
                else if (Match(text, ref i, "~"))
                    kind = KindTilde;
                else if (Match(text, ref i, "^"))
                    kind = KindCaret;

                SkipSeparators(text, ref i);

                int start = i;
                while (i < text.Length && !IsSeparator(text[i]))
                    i++;
                if (i == start)
                    return false;

                SemVersion version;
                int components;
                if (!SemVersion.TryParsePartial(text.Substring(start, i - start), out version, out components))
                    return false;

                Simple simple = new Simple();
                simple.Kind = kind;
                simple.Low = version;
                simple.High = version;

                if (kind == KindTilde)
                {
                    SemVersion high = version;
                    high.PreRelease = null;
                    if (components >= 2)
                    {
                        high.Major = version.Major;
                        high.Minor = version.Minor + 1;
                        high.Patch = 0;
                    }
                    else
                    {
                        high.Major = version.Major + 1;
                        high.Minor = 0;
                        high.Patch = 0;
                    }
                    simple.High = high;
                    simple.Text = ">=" + version.ToString() + " <" + high.ToString();
                }
                else if (kind == KindCaret)
                {
                    SemVersion high = version;
                    high.PreRelease = null;
                    if (version.Major > 0)
                    {
                        high.Major = version.Major + 1;
                        high.Minor = 0;
                        high.Patch = 0;
                    }
                    else if (version.Minor > 0)
                    {
                        high.Major = 0;
                        high.Minor = version.Minor + 1;
                        high.Patch = 0;
                    }
                    else
                    {
                        high.Major = 0;
                        high.Minor = 0;
                        high.Patch = version.Patch + 1;
                    }
                    simple.High = high;
                    simple.Text = ">=" + version.ToString() + " <" + high.ToString();
                }
                else if (kind == KindEqual)
                {
                    simple.Text = "=" + version.ToString();
                }
                else if (kind == KindGreater)
                {
                    simple.Text = ">" + version.ToString();
                }
                else if (kind == KindGreaterEqual)
                {
                    simple.Text = ">=" + version.ToString();
                }
                else if (kind == KindLess)
                {
                    simple.Text = "<" + version.ToString();
                }
                else
                {
                    simple.Text = "<=" + version.ToString();
                }

                parts.Add(simple);
            }

            if (parts.Count == 0)
                return false;

            VersionConstraint result = new VersionConstraint();
            for (int p = 0; p < parts.Count; p++)
                result._parts.Add(parts[p]);
            constraint = result;
            return true;
        }

        /// <summary>
        /// Phase 8: evaluates the constraint against a candidate version;
        /// every simple constraint must match (logical AND).
        /// </summary>
        public bool Matches(SemVersion version)
        {
            for (int i = 0; i < _parts.Count; i++)
            {
                Simple part = _parts[i];
                int cmp = version.CompareTo(part.Low);

                if (part.Kind == KindEqual)
                {
                    if (cmp != 0)
                        return false;
                }
                else if (part.Kind == KindGreater)
                {
                    if (cmp <= 0)
                        return false;
                }
                else if (part.Kind == KindGreaterEqual)
                {
                    if (cmp < 0)
                        return false;
                }
                else if (part.Kind == KindLess)
                {
                    if (cmp >= 0)
                        return false;
                }
                else if (part.Kind == KindLessEqual)
                {
                    if (cmp > 0)
                        return false;
                }
                else
                {
                    // ~ or ^: pre-releases never match a range (documented simplification).
                    if (version.PreRelease != null && version.PreRelease.Length > 0)
                        return false;
                    if (cmp < 0)
                        return false;
                    if (version.CompareTo(part.High) >= 0)
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Phase 8: canonical text form; "~1.2.3" is re-emitted as its
        /// equivalent range "&gt;=1.2.3 &lt;1.3.0" (simple constraints joined
        /// by single spaces).
        /// </summary>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < _parts.Count; i++)
            {
                if (i > 0)
                    sb.Append(' ');
                sb.Append(_parts[i].Text);
            }
            return sb.ToString();
        }

        private static bool Match(string text, ref int i, string op)
        {
            if (i + op.Length > text.Length)
                return false;
            for (int k = 0; k < op.Length; k++)
            {
                if (text[i + k] != op[k])
                    return false;
            }
            i += op.Length;
            return true;
        }

        private static bool IsSeparator(char c)
        {
            return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == ',';
        }

        private static void SkipSeparators(string text, ref int i)
        {
            while (i < text.Length && IsSeparator(text[i]))
                i++;
        }
    }
}
