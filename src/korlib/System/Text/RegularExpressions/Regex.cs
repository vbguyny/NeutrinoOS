// NeutrinoOS korlib - System.Text.RegularExpressions (public surface)
//
// Humanizer.Core 3.0.10 (and any other .NET 7+ consumer) gets its regexes from
// the [GeneratedRegex] source generator, which emits, into the CONSUMER
// assembly, subclasses of Regex that poke five of its fields and call one static
// helper directly:
//
//   call   Regex::.ctor()                        (parameterless base ctor)
//   ldstr  "<pattern>"; stfld Regex::pattern
//   ldc.i4 0;           stfld Regex::roptions
//   call   Regex::ValidateMatchTimeout(TimeSpan)
//   stfld  Regex::internalMatchTimeout
//   newobj <generated RunnerFactory>;  stfld Regex::factory
//   ldc.i4 N;           stfld Regex::capsize
//
// So the field NAMES and types below are part of the ABI with that generated
// code; they are public here (rather than private as in the BCL) so the JIT's
// by-name member resolution always succeeds.
//
// The matching itself is done by RegexEngine (a backtracking matcher over the
// pattern text). The generated RegexRunner subclass is never invoked - korlib
// has no RegexRunner state machine - which is why `factory` is stored and
// ignored.

using System.Collections;

namespace System.Text.RegularExpressions
{
    [Flags]
    public enum RegexOptions
    {
        None = 0,
        IgnoreCase = 1,
        Multiline = 2,
        ExplicitCapture = 4,
        Compiled = 8,
        Singleline = 16,
        IgnorePatternWhitespace = 32,
        RightToLeft = 64,
        ECMAScript = 256,
        CultureInvariant = 512,
        NonBacktracking = 1024,
    }

    /// <summary>Marks a partial method whose body the source generator supplies.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class GeneratedRegexAttribute : Attribute
    {
        public GeneratedRegexAttribute(string pattern)
            : this(pattern, RegexOptions.None, -1, null) { }

        public GeneratedRegexAttribute(string pattern, RegexOptions options)
            : this(pattern, options, -1, null) { }

        public GeneratedRegexAttribute(string pattern, RegexOptions options, string cultureName)
            : this(pattern, options, -1, cultureName) { }

        public GeneratedRegexAttribute(string pattern, RegexOptions options, int matchTimeoutMilliseconds)
            : this(pattern, options, matchTimeoutMilliseconds, null) { }

        public GeneratedRegexAttribute(string pattern, RegexOptions options, int matchTimeoutMilliseconds, string? cultureName)
        {
            Pattern = pattern;
            Options = options;
            MatchTimeoutMilliseconds = matchTimeoutMilliseconds;
            CultureName = cultureName;
        }

        public string Pattern { get; }
        public RegexOptions Options { get; }
        public int MatchTimeoutMilliseconds { get; }
        public string? CultureName { get; }
    }

    public delegate string MatchEvaluator(Match match);

    public class Regex
    {
        // --- ABI with generated regex subclasses (see file header) ---
        public string? pattern;
        public RegexOptions roptions;
        public TimeSpan internalMatchTimeout;
        public RegexRunnerFactory? factory;
        public int capsize;

        private RegexEngineImpl? _engine;

        /// <summary>Used by source-generated subclasses, which then set the fields.</summary>
        public Regex()
        {
            internalMatchTimeout = TimeSpan.FromMilliseconds(-1);
        }

        public Regex(string pattern) : this(pattern, RegexOptions.None) { }

        public Regex(string pattern, RegexOptions options)
        {
            this.pattern = pattern;
            roptions = options;
            internalMatchTimeout = TimeSpan.FromMilliseconds(-1);
        }

        public Regex(string pattern, RegexOptions options, TimeSpan matchTimeout)
            : this(pattern, options)
        {
            internalMatchTimeout = matchTimeout;
        }

        // Generated subclasses call this with Utilities.s_defaultTimeout (-1 ms
        // = infinite). Nothing to validate on device.
        public static void ValidateMatchTimeout(TimeSpan matchTimeout) { }

        public string Pattern => pattern ?? string.Empty;
        public RegexOptions Options => roptions;
        public TimeSpan MatchTimeout => internalMatchTimeout;

        private RegexEngineImpl Engine => _engine ??= RegexEngine.Compile(pattern ?? string.Empty, roptions);

        public bool IsMatch(string input) => input != null && Engine.Find(input, 0, out _);

        public bool IsMatch(string input, int startat) => input != null && Engine.Find(input, startat, out _);

        public Match Match(string input) => Match(input, 0);

        public Match Match(string input, int startat)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (Engine.Find(input, startat, out MatchData? data) && data != null)
                return new Match(this, input, data);
            return NewEmpty(input);
        }

        private Match NewEmpty(string input) => new Match(this, input);

        public MatchCollection Matches(string input) => Matches(input, 0);

        public MatchCollection Matches(string input, int startat)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var list = new Match[8];
            int count = 0;
            int pos = startat;
            while (pos <= input.Length && Engine.Find(input, pos, out MatchData? data) && data != null)
            {
                if (count == list.Length)
                {
                    var bigger = new Match[list.Length * 2];
                    for (int i = 0; i < count; i++) bigger[i] = list[i];
                    list = bigger;
                }
                list[count++] = new Match(this, input, data);
                // Empty match: advance one char to avoid an infinite loop.
                pos = data.End > data.Start ? data.End : data.End + 1;
            }
            var result = new Match[count];
            for (int i = 0; i < count; i++) result[i] = list[i];
            return new MatchCollection(result);
        }

        public string Replace(string input, MatchEvaluator evaluator)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (evaluator == null) throw new ArgumentNullException(nameof(evaluator));
            var sb = new StringBuilder();
            int pos = 0;
            while (pos <= input.Length && Engine.Find(input, pos, out MatchData? data) && data != null)
            {
                for (int i = pos; i < data.Start; i++) sb.Append(input[i]);
                sb.Append(evaluator(new Match(this, input, data)));
                pos = data.End > data.Start ? data.End : data.End + 1;
                if (pos > input.Length) break;
            }
            for (int i = pos < input.Length ? pos : input.Length; i < input.Length; i++) sb.Append(input[i]);
            return sb.ToString();
        }

        public string Replace(string input, string replacement)
            => ReplaceCore(input, replacement, -1);

        public string Replace(string input, string replacement, int count)
            => ReplaceCore(input, replacement, count);

        private string ReplaceCore(string input, string replacement, int maxCount)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));
            var sb = new StringBuilder();
            int pos = 0;
            int done = 0;
            while ((maxCount < 0 || done < maxCount) && pos <= input.Length &&
                   Engine.Find(input, pos, out MatchData? data) && data != null)
            {
                for (int i = pos; i < data.Start; i++) sb.Append(input[i]);
                var match = new Match(this, input, data);
                for (int i = 0; i < replacement.Length; i++)
                {
                    char ch = replacement[i];
                    if (ch != '$' || i + 1 >= replacement.Length) { sb.Append(ch); continue; }
                    char n = replacement[i + 1];
                    if (n == '$') { sb.Append('$'); i++; }
                    else if (n == '&') { sb.Append(match.Value); i++; }
                    else if (n >= '0' && n <= '9') { sb.Append(match.Groups[n - '0'].Value); i++; }
                    else sb.Append(ch);
                }
                pos = data.End > data.Start ? data.End : data.End + 1;
                done++;
                if (pos > input.Length) break;
            }
            for (int i = pos < input.Length ? pos : input.Length; i < input.Length; i++) sb.Append(input[i]);
            return sb.ToString();
        }

        public string[] Split(string input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var parts = new string[8];
            int count = 0;
            int pos = 0;
            int last = 0;
            while (pos <= input.Length && Engine.Find(input, pos, out MatchData? data) && data != null)
            {
                if (count == parts.Length)
                {
                    var bigger = new string[parts.Length * 2];
                    for (int i = 0; i < count; i++) bigger[i] = parts[i];
                    parts = bigger;
                }
                parts[count++] = input.Substring(last, data.Start - last);
                last = data.End;
                pos = data.End > data.Start ? data.End : data.End + 1;
            }
            if (count == parts.Length)
            {
                var bigger = new string[parts.Length + 1];
                for (int i = 0; i < count; i++) bigger[i] = parts[i];
                parts = bigger;
            }
            parts[count++] = input.Substring(last, input.Length - last);
            var result = new string[count];
            for (int i = 0; i < count; i++) result[i] = parts[i];
            return result;
        }

        public override string ToString() => Pattern;
    }

    /// <summary>One captured span.</summary>
    public class Capture
    {
        internal readonly string _input;
        internal readonly int _index;
        internal readonly int _length;

        internal Capture(string input, int index, int length)
        {
            _input = input;
            _index = index;
            _length = length;
        }

        public int Index => _index;
        public int Length => _length;
        public string Value => _input.Substring(_index, _length);
        public override string ToString() => Value;
    }

    public class Group : Capture
    {
        internal readonly bool _success;
        internal readonly Capture[] _captures;

        internal Group(string input, int index, int length, bool success)
            : base(input, index, length)
        {
            _success = success;
            _captures = success ? new[] { new Capture(input, index, length) } : new Capture[0];
        }

        public bool Success => _success;
        public CaptureCollection Captures => new CaptureCollection(_captures);
    }

    public class CaptureCollection : IEnumerable
    {
        private readonly Capture[] _items;
        internal CaptureCollection(Capture[] items) { _items = items; }
        public int Count => _items.Length;
        public Capture this[int i] => _items[i];
        public IEnumerator GetEnumerator() => new CaptureEnumerator(_items);
    }

    public class GroupCollection : IEnumerable
    {
        private readonly Group[] _items;
        internal GroupCollection(Group[] items) { _items = items; }
        public int Count => _items.Length;
        public Group this[int i] => i >= 0 && i < _items.Length ? _items[i] : _items[0];
        public IEnumerator GetEnumerator() => new GroupEnumerator(_items);
    }

    public class Match : Group
    {
        private readonly Regex _regex;

        internal Match(Regex regex, string input, MatchData data)
            : base(input, data.Start, data.End - data.Start, true)
        {
            _regex = regex;
            var groups = new Group[data.GroupCount];
            for (int i = 0; i < groups.Length; i++)
            {
                int s = data.GroupStart(i);
                int e = data.GroupEnd(i);
                groups[i] = s < 0
                    ? new Group(input, 0, 0, false)
                    : new Group(input, s, e - s, true);
            }
            Groups = new GroupCollection(groups);
        }

        internal Match(Regex regex, string input)
            : base(input, 0, 0, false)
        {
            _regex = regex;
            var groups = new Group[1];
            groups[0] = new Group(input, 0, 0, false);
            Groups = new GroupCollection(groups);
        }

        internal static Match CreateEmpty(Regex regex, string input) => new Match(regex, input);

        public GroupCollection Groups { get; }

        public Match NextMatch()
        {
            int start = Index + (Length > 0 ? Length : 1);
            if (start > _input.Length) return CreateEmpty(_regex, _input);
            return _regex.Match(_input, start);
        }
    }

    public class MatchCollection : IEnumerable
    {
        private readonly Match[] _items;
        internal MatchCollection(Match[] items) { _items = items; }
        public int Count => _items.Length;
        public Match this[int i] => _items[i];
        public IEnumerator GetEnumerator() => new MatchEnumerator(_items);
    }

    internal sealed class MatchEnumerator : IEnumerator
    {
        private readonly Match[] _items;
        private int _index = -1;
        internal MatchEnumerator(Match[] items) { _items = items; }
        public object? Current => _index >= 0 && _index < _items.Length ? _items[_index] : null;
        public bool MoveNext() => ++_index < _items.Length;
        public void Reset() => _index = -1;
    }

    internal sealed class GroupEnumerator : IEnumerator
    {
        private readonly Group[] _items;
        private int _index = -1;
        internal GroupEnumerator(Group[] items) { _items = items; }
        public object? Current => _index >= 0 && _index < _items.Length ? _items[_index] : null;
        public bool MoveNext() => ++_index < _items.Length;
        public void Reset() => _index = -1;
    }

    internal sealed class CaptureEnumerator : IEnumerator
    {
        private readonly Capture[] _items;
        private int _index = -1;
        internal CaptureEnumerator(Capture[] items) { _items = items; }
        public object? Current => _index >= 0 && _index < _items.Length ? _items[_index] : null;
        public bool MoveNext() => ++_index < _items.Length;
        public void Reset() => _index = -1;
    }

    /// <summary>
    /// Base class for source-generated runners. korlib matches with RegexEngine
    /// instead of a generated state machine, so this only needs to exist (and be
    /// constructible) for the generator's derived types to load.
    /// </summary>
    public abstract class RegexRunner
    {
        public int runtextpos;
        public int[]? runstack;

        protected RegexRunner() { }

        protected static bool CharInClass(char ch, string set) => RegexEngine.CharInClassText(ch, set);
        protected int Crawlpos() => 0;
        protected void Capture(int capnum, int start, int end) { }
        protected void Uncapture() { }
        protected void CheckTimeout() { }
    }

    /// <summary>Base class for source-generated runner factories.</summary>
    public abstract class RegexRunnerFactory
    {
        protected RegexRunnerFactory() { }
        protected internal abstract RegexRunner CreateInstance();
    }

    /// <summary>Result of a successful engine match: capture slots for every group.</summary>
    internal sealed class MatchData
    {
        internal readonly int Start;
        internal readonly int End;
        private readonly int[] _starts;
        private readonly int[] _ends;

        internal MatchData(int start, int end, int[] starts, int[] ends)
        {
            Start = start;
            End = end;
            _starts = starts;
            _ends = ends;
        }

        internal int GroupCount => _starts.Length;
        internal int GroupStart(int i) => i >= 0 && i < _starts.Length ? _starts[i] : -1;
        internal int GroupEnd(int i) => i >= 0 && i < _ends.Length ? _ends[i] : -1;
    }
}
