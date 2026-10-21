// NeutrinoOS korlib - regex engine used by System.Text.RegularExpressions.Regex
//
// A small backtracking matcher. It parses the pattern into a node tree and
// matches with continuation passing (each node is handed a continuation to run
// on the remainder), which keeps the backtracking rules simple to reason about.
//
// Supported syntax: literals, '.', character classes with ranges/negation,
// \d \D \w \W \s \S \b \B, \p{Lu} \p{Ll} \p{Lo} \p{L} \p{N}, \n \r \t \f \v \0,
// \uXXXX, quantifiers * + ? {n} {n,} {n,m} with a lazy '?' suffix, capturing
// groups, (?: ) non-capturing groups, (?= ) / (?! ) lookahead, alternation, and
// the ^ $ anchors.
//
// Not supported (not reachable from the packages on device so far): lookbehind,
// backreferences, atomic groups, conditionals, balancing groups, and the
// RightToLeft/NonBacktracking modes. Parsing one of those throws
// NotSupportedException naming the construct, so a future package surfaces it
// as a clear device error instead of silently mismatching.

namespace System.Text.RegularExpressions
{
    internal static class RegexEngine
    {
        internal static RegexEngineImpl Compile(string pattern, RegexOptions options)
        {
            var parser = new RegexParser(pattern, options);
            RNode root = parser.ParseRoot();
            return new RegexEngineImpl(root, options, parser.GroupCount);
        }

        /// <summary>Character-class test used by the RegexRunner compatibility stub.</summary>
        internal static bool CharInClassText(char ch, string set) => RegexEngineImpl.CharInClassText(ch, set);
    }

    /// <summary>Continuation: try to match the rest of the pattern at pos.</summary>
    internal interface ICont
    {
        bool Run(RegexMatcher m, int pos);
    }

    internal sealed class RegexEngineImpl
    {
        private readonly RNode _root;
        private readonly RegexOptions _options;
        private readonly int _groupCount;

        internal RegexEngineImpl(RNode root, RegexOptions options, int groupCount)
        {
            _root = root;
            _options = options;
            _groupCount = groupCount;
        }

        internal bool Find(string input, int startat, out MatchData? data)
        {
            data = null;
            if (startat < 0) startat = 0;
            if (startat > input.Length) return false;

            var m = new RegexMatcher(input, _options, _groupCount);
            var end = new EndCont();
            for (int i = startat; i <= input.Length; i++)
            {
                m.Reset();
                if (!_root.Match(m, i, end)) continue;
                m.SetGroup(0, i, m.MatchEnd);
                data = m.ToMatchData(i);
                return true;
            }
            return false;
        }

        /// <summary>Character-class test used by the RegexRunner compatibility stub.</summary>
        internal static bool CharInClassText(char ch, string set)
        {
            if (set == null || set.Length == 0) return false;
            bool negate = set[0] == '^';
            int i = negate ? 1 : 0;
            bool hit = false;
            while (i < set.Length)
            {
                if (i + 2 < set.Length && set[i + 1] == '-')
                {
                    if (ch >= set[i] && ch <= set[i + 2]) hit = true;
                    i += 3;
                }
                else
                {
                    if (ch == set[i]) hit = true;
                    i++;
                }
            }
            return negate ? !hit : hit;
        }
    }

    internal sealed class RegexMatcher
    {
        internal readonly string Text;
        internal readonly RegexOptions Options;
        internal int MatchEnd;
        private readonly int[] _starts;
        private readonly int[] _ends;

        internal RegexMatcher(string text, RegexOptions options, int groupCount)
        {
            Text = text;
            Options = options;
            _starts = new int[groupCount];
            _ends = new int[groupCount];
            Reset();
        }

        internal int Length => Text.Length;
        internal bool IgnoreCase => (Options & RegexOptions.IgnoreCase) != 0;
        internal bool Multiline => (Options & RegexOptions.Multiline) != 0;
        internal bool Singleline => (Options & RegexOptions.Singleline) != 0;

        internal void Reset()
        {
            for (int i = 0; i < _starts.Length; i++) { _starts[i] = -1; _ends[i] = -1; }
            MatchEnd = -1;
        }

        internal void SetGroup(int index, int start, int end)
        {
            if (index >= 0 && index < _starts.Length) { _starts[index] = start; _ends[index] = end; }
        }

        internal int GetGroupStart(int index) => _starts[index];
        internal int GetGroupEnd(int index) => _ends[index];

        internal MatchData ToMatchData(int start)
        {
            var s = new int[_starts.Length];
            var e = new int[_ends.Length];
            for (int i = 0; i < s.Length; i++) { s[i] = _starts[i]; e[i] = _ends[i]; }
            return new MatchData(start, MatchEnd, s, e);
        }
    }

    // =========================================================================
    // Nodes
    // =========================================================================
    internal abstract class RNode
    {
        internal abstract bool Match(RegexMatcher m, int pos, ICont k);

        internal static bool CharEq(char a, char b, bool ignoreCase)
        {
            if (a == b) return true;
            if (!ignoreCase) return false;
            char ua = Char.ToUpper(a), ub = Char.ToUpper(b);
            return ua == ub || Char.ToLower(a) == Char.ToLower(b);
        }
    }

    internal sealed class EndCont : ICont
    {
        public bool Run(RegexMatcher m, int pos) { m.MatchEnd = pos; return true; }
    }

    internal sealed class SeqCont : ICont
    {
        internal RNode[] Items = null!;
        internal int Index;
        internal ICont Next = null!;

        public bool Run(RegexMatcher m, int pos)
        {
            var node = Items[Index];
            ICont cont = Index + 1 < Items.Length
                ? new SeqCont { Items = Items, Index = Index + 1, Next = Next }
                : Next;
            return node.Match(m, pos, cont);
        }
    }

    internal sealed class RSeq : RNode
    {
        internal RNode[] Items = null!;

        internal override bool Match(RegexMatcher m, int pos, ICont k)
        {
            if (Items.Length == 0) return k.Run(m, pos);
            var first = new SeqCont { Items = Items, Index = 0, Next = k };
            return first.Run(m, pos);
        }
    }

    internal sealed class RAlt : RNode
    {
        internal RNode[] Alts = null!;

        internal override bool Match(RegexMatcher m, int pos, ICont k)
        {
            for (int i = 0; i < Alts.Length; i++)
                if (Alts[i].Match(m, pos, k)) return true;
            return false;
        }
    }

    internal sealed class RChar : RNode
    {
        internal char Ch;

        internal override bool Match(RegexMatcher m, int pos, ICont k)
            => pos < m.Length && CharEq(m.Text[pos], Ch, m.IgnoreCase) && k.Run(m, pos + 1);
    }

    internal sealed class RAny : RNode
    {
        internal override bool Match(RegexMatcher m, int pos, ICont k)
            => pos < m.Length && (m.Singleline || m.Text[pos] != '\n') && k.Run(m, pos + 1);
    }

    internal sealed class RAnchorStart : RNode
    {
        internal override bool Match(RegexMatcher m, int pos, ICont k)
        {
            if (pos == 0) return k.Run(m, pos);
            if (m.Multiline && pos > 0 && m.Text[pos - 1] == '\n') return k.Run(m, pos);
            return false;
        }
    }

    internal sealed class RAnchorEnd : RNode
    {
        internal override bool Match(RegexMatcher m, int pos, ICont k)
        {
            if (pos == m.Length) return k.Run(m, pos);
            if (pos == m.Length - 1 && m.Text[pos] == '\n') return k.Run(m, pos);
            if (m.Multiline && pos < m.Length && m.Text[pos] == '\n') return k.Run(m, pos);
            return false;
        }
    }

    internal sealed class RBoundary : RNode
    {
        internal bool Negate;

        private static bool IsWord(char c) => Char.IsLetterOrDigit(c) || c == '_';

        internal override bool Match(RegexMatcher m, int pos, ICont k)
        {
            bool before = pos > 0 && IsWord(m.Text[pos - 1]);
            bool after = pos < m.Length && IsWord(m.Text[pos]);
            bool boundary = before != after;
            return (boundary != Negate) && k.Run(m, pos);
        }
    }

    internal sealed class RGroup : RNode
    {
        internal RNode Inner = null!;
        internal int Index = -1;   // -1 = non-capturing

        internal override bool Match(RegexMatcher m, int pos, ICont k)
        {
            if (Index < 0) return Inner.Match(m, pos, k);
            return Inner.Match(m, pos, new GroupCont { Owner = this, Start = pos, Next = k });
        }
    }

    internal sealed class GroupCont : ICont
    {
        internal RGroup Owner = null!;
        internal int Start;
        internal ICont Next = null!;

        public bool Run(RegexMatcher m, int pos)
        {
            int ps = m.GetGroupStart(Owner.Index);
            int pe = m.GetGroupEnd(Owner.Index);
            m.SetGroup(Owner.Index, Start, pos);
            if (Next.Run(m, pos)) return true;
            m.SetGroup(Owner.Index, ps, pe);
            return false;
        }
    }

    internal sealed class RLook : RNode
    {
        internal RNode Inner = null!;
        internal bool Negative;

        internal override bool Match(RegexMatcher m, int pos, ICont k)
        {
            bool matched = Inner.Match(m, pos, new EndCont());
            if (matched == Negative) return false;
            return k.Run(m, pos);
        }
    }

    internal sealed class RRepeat : RNode
    {
        internal RNode Inner = null!;
        internal int Min;
        internal int Max = -1;    // -1 = unbounded
        internal bool Lazy;

        internal override bool Match(RegexMatcher m, int pos, ICont k)
            => Rep(m, pos, 0, -1, k);

        internal bool Rep(RegexMatcher m, int pos, int count, int lastPos, ICont k)
        {
            bool canMore = Max < 0 || count < Max;
            if (Lazy)
            {
                if (count >= Min && k.Run(m, pos)) return true;
                if (!canMore) return false;
                if (count > Min && pos == lastPos) return false;   // empty iteration guard
                return Inner.Match(m, pos, new RepCont { Owner = this, Count = count + 1, LastPos = pos, Next = k });
            }
            bool progressed = count == 0 || pos != lastPos;
            if (canMore && progressed &&
                Inner.Match(m, pos, new RepCont { Owner = this, Count = count + 1, LastPos = pos, Next = k }))
                return true;
            return count >= Min && k.Run(m, pos);
        }
    }

    internal sealed class RepCont : ICont
    {
        internal RRepeat Owner = null!;
        internal int Count;
        internal int LastPos;
        internal ICont Next = null!;

        public bool Run(RegexMatcher m, int pos) => Owner.Rep(m, pos, Count, LastPos, Next);
    }

    // =========================================================================
    // Character classes
    // =========================================================================
    internal enum ClassCat
    {
        Range = 0, Digit, WordChar, Space, Upper, Lower, Letter, LetterOther, Number,
    }

    internal struct ClassItem
    {
        internal char Lo;
        internal char Hi;
        internal ClassCat Cat;

        internal static ClassItem MakeRange(char lo, char hi) => new ClassItem { Lo = lo, Hi = hi, Cat = ClassCat.Range };
        internal static ClassItem MakeCat(ClassCat cat) => new ClassItem { Cat = cat };

        internal bool Contains(char c)
        {
            switch (Cat)
            {
                case ClassCat.Range: return c >= Lo && c <= Hi;
                case ClassCat.Digit: return Char.IsDigit(c);
                case ClassCat.WordChar: return Char.IsLetterOrDigit(c) || c == '_';
                case ClassCat.Space: return Char.IsWhiteSpace(c);
                case ClassCat.Upper: return Char.IsUpper(c);
                case ClassCat.Lower: return Char.IsLower(c);
                case ClassCat.Letter: return Char.IsLetter(c);
                case ClassCat.LetterOther: return Char.IsLetter(c) && !Char.IsUpper(c) && !Char.IsLower(c);
                case ClassCat.Number: return Char.IsDigit(c);
                default: return false;
            }
        }
    }

    internal sealed class RClass : RNode
    {
        internal ClassItem[] Items = null!;
        internal bool Negate;

        internal bool Contains(char c)
        {
            for (int i = 0; i < Items.Length; i++)
                if (Items[i].Contains(c)) return !Negate;
            return Negate;
        }

        internal override bool Match(RegexMatcher m, int pos, ICont k)
        {
            if (pos >= m.Length) return false;
            char c = m.Text[pos];
            if (!Contains(c))
            {
                // IgnoreCase: retry with the alternate case form.
                if (!m.IgnoreCase) return false;
                char alt = Char.IsUpper(c) ? Char.ToLower(c) : Char.ToUpper(c);
                if (alt == c || !Contains(alt)) return false;
            }
            return k.Run(m, pos + 1);
        }
    }

    // =========================================================================
    // Parser
    // =========================================================================
    internal sealed class RegexParser
    {
        private readonly string _p;
        private int _i;
        private int _groups;
        private bool _explicitCapture;

        internal RegexParser(string pattern, RegexOptions options)
        {
            _p = pattern ?? string.Empty;
            _i = 0;
            _groups = 1;   // group 0 = whole match
            _explicitCapture = (options & RegexOptions.ExplicitCapture) != 0;
        }

        internal int GroupCount => _groups;

        internal RNode ParseRoot()
        {
            var node = ParseAlt();
            if (_i < _p.Length)
                throw new NotSupportedException("regex: unbalanced ')' in '" + _p + "'");
            return node;
        }

        private bool AtEnd => _i >= _p.Length;
        private char Peek => _p[_i];

        private RNode ParseAlt()
        {
            var first = ParseSeq();
            if (AtEnd || Peek != '|') return first;

            var alts = new System.Collections.Generic.List<RNode>();
            alts.Add(first);
            while (!AtEnd && Peek == '|')
            {
                _i++;
                alts.Add(ParseSeq());
            }
            return new RAlt { Alts = alts.ToArray() };
        }

        private RNode ParseSeq()
        {
            var items = new System.Collections.Generic.List<RNode>();
            while (!AtEnd && Peek != '|' && Peek != ')')
                items.Add(ParseTerm());
            if (items.Count == 0) return new RSeq { Items = new RNode[0] };
            if (items.Count == 1) return items[0];
            return new RSeq { Items = items.ToArray() };
        }

        private RNode ParseTerm()
        {
            var atom = ParseAtom();
            if (AtEnd) return atom;

            int min, max;
            char c = Peek;
            if (c == '*') { min = 0; max = -1; _i++; }
            else if (c == '+') { min = 1; max = -1; _i++; }
            else if (c == '?') { min = 0; max = 1; _i++; }
            else if (c == '{' && TryParseBraceQuantifier(out min, out max)) { }
            else return atom;

            bool lazy = !AtEnd && Peek == '?';
            if (lazy) _i++;
            return new RRepeat { Inner = atom, Min = min, Max = max, Lazy = lazy };
        }

        // Parses {n} {n,} {n,m}; leaves _i untouched and returns false when the
        // brace is not a quantifier (then it is a literal).
        private bool TryParseBraceQuantifier(out int min, out int max)
        {
            min = 0;
            max = 0;
            int save = _i;
            _i++;   // '{'
            if (AtEnd || !Char.IsDigit(Peek)) { _i = save; return false; }
            min = ReadInt();
            max = min;
            if (!AtEnd && Peek == ',')
            {
                _i++;
                if (!AtEnd && Char.IsDigit(Peek)) max = ReadInt();
                else max = -1;
            }
            if (AtEnd || Peek != '}') { _i = save; return false; }
            _i++;   // '}'
            return true;
        }

        private int ReadInt()
        {
            int v = 0;
            while (!AtEnd && Char.IsDigit(Peek))
            {
                v = v * 10 + (Peek - '0');
                _i++;
            }
            return v;
        }

        private RNode ParseAtom()
        {
            char c = Peek;
            if (c == '(') return ParseGroup();
            if (c == '[') return ParseClass();
            if (c == '.') { _i++; return new RAny(); }
            if (c == '^') { _i++; return new RAnchorStart(); }
            if (c == '$') { _i++; return new RAnchorEnd(); }
            if (c == '\\') return ParseEscape();
            _i++;
            return new RChar { Ch = c };
        }

        private RNode ParseGroup()
        {
            _i++;   // '('
            if (!AtEnd && Peek == '?')
            {
                _i++;
                if (!AtEnd && Peek == ':') { _i++; var inner = ParseAlt(); Expect(')'); return new RGroup { Inner = inner, Index = -1 }; }
                if (!AtEnd && Peek == '=') { _i++; var inner = ParseAlt(); Expect(')'); return new RLook { Inner = inner, Negative = false }; }
                if (!AtEnd && Peek == '!') { _i++; var inner = ParseAlt(); Expect(')'); return new RLook { Inner = inner, Negative = true }; }
                throw new NotSupportedException("regex: '(?' construct is not supported by korlib's engine");
            }

            int index = _explicitCapture ? -1 : _groups++;
            var body = ParseAlt();
            Expect(')');
            return new RGroup { Inner = body, Index = index };
        }

        private void Expect(char c)
        {
            if (AtEnd || Peek != c)
                throw new NotSupportedException("regex: expected '" + c + "'");
            _i++;
        }

        private RNode ParseEscape()
        {
            _i++;   // '\'
            if (AtEnd) throw new NotSupportedException("regex: trailing '\\'");
            char c = Peek;
            _i++;
            switch (c)
            {
                case 'd': return new RClass { Items = new[] { ClassItem.MakeCat(ClassCat.Digit) } };
                case 'D': return new RClass { Items = new[] { ClassItem.MakeCat(ClassCat.Digit) }, Negate = true };
                case 'w': return new RClass { Items = new[] { ClassItem.MakeCat(ClassCat.WordChar) } };
                case 'W': return new RClass { Items = new[] { ClassItem.MakeCat(ClassCat.WordChar) }, Negate = true };
                case 's': return new RClass { Items = new[] { ClassItem.MakeCat(ClassCat.Space) } };
                case 'S': return new RClass { Items = new[] { ClassItem.MakeCat(ClassCat.Space) }, Negate = true };
                case 'b': return new RBoundary { Negate = false };
                case 'B': return new RBoundary { Negate = true };
                case 'n': return new RChar { Ch = '\n' };
                case 'r': return new RChar { Ch = '\r' };
                case 't': return new RChar { Ch = '\t' };
                case 'f': return new RChar { Ch = '\f' };
                case 'v': return new RChar { Ch = '\v' };
                case '0': return new RChar { Ch = '\0' };
                case 'p':
                case 'P':
                    {
                        Expect('{');
                        int start = _i;
                        while (!AtEnd && Peek != '}') _i++;
                        string name = _p.Substring(start, _i - start);
                        Expect('}');
                        return new RClass
                        {
                            Items = new[] { ClassItem.MakeCat(CategoryFromName(name)) },
                            Negate = c == 'P',
                        };
                    }
                case 'u':
                    {
                        int v = 0;
                        for (int k = 0; k < 4 && !AtEnd; k++)
                        {
                            char h = Peek;
                            _i++;
                            v = v * 16 + (Char.IsDigit(h) ? h - '0' : (Char.ToUpper(h) - 'A' + 10));
                        }
                        return new RChar { Ch = (char)v };
                    }
                default:
                    return new RChar { Ch = c };   // \. \\ \+ ... = literal
            }
        }

        internal static ClassCat CategoryFromName(string name)
        {
            switch (name)
            {
                case "Lu": return ClassCat.Upper;
                case "Ll": return ClassCat.Lower;
                case "Lo": return ClassCat.LetterOther;
                case "L": return ClassCat.Letter;
                case "N":
                case "Nd": return ClassCat.Number;
                default: return ClassCat.Letter;
            }
        }

        private RNode ParseClass()
        {
            _i++;   // '['
            bool negate = false;
            if (!AtEnd && Peek == '^') { negate = true; _i++; }

            var items = new System.Collections.Generic.List<ClassItem>();
            while (!AtEnd && Peek != ']')
            {
                // Low end of a possible range: either an escape or a literal.
                bool lowIsChar;
                char lo = '\0';
                if (Peek == '\\')
                {
                    var esc = ParseEscape();
                    if (esc is RChar rc) { lowIsChar = true; lo = rc.Ch; }
                    else
                    {
                        // \d \w \s \p{..} inside a class: category items, no range.
                        lowIsChar = false;
                        if (esc is RClass rcl)
                        {
                            for (int j = 0; j < rcl.Items.Length; j++) items.Add(rcl.Items[j]);
                        }
                        else if (esc is RBoundary) items.Add(ClassItem.MakeRange('\b', '\b'));
                    }
                }
                else
                {
                    lowIsChar = true;
                    lo = Peek;
                    _i++;
                }

                if (!lowIsChar) continue;

                if (!AtEnd && Peek == '-' && _i + 1 < _p.Length && _p[_i + 1] != ']')
                {
                    _i++;   // '-'
                    char hi = '\0';
                    bool hiOk = true;
                    if (!AtEnd && Peek == '\\')
                    {
                        var esc2 = ParseEscape();
                        if (esc2 is RChar rc2) hi = rc2.Ch;
                        else
                        {
                            // Not a plain char (e.g. [a-\d]): treat the '-'
                            // literally and keep the category item.
                            hiOk = false;
                            items.Add(ClassItem.MakeRange(lo, lo));
                            items.Add(ClassItem.MakeRange('-', '-'));
                            if (esc2 is RClass rcl2)
                                for (int j = 0; j < rcl2.Items.Length; j++) items.Add(rcl2.Items[j]);
                        }
                    }
                    else if (!AtEnd)
                    {
                        hi = Peek;
                        _i++;
                    }
                    else hiOk = false;

                    if (hiOk) items.Add(ClassItem.MakeRange(lo, hi));
                }
                else
                {
                    items.Add(ClassItem.MakeRange(lo, lo));
                }
            }
            Expect(']');
            return new RClass { Items = items.ToArray(), Negate = negate };
        }
    }
}
