// thirdprobe - M8.1 boundary probes (docs/NUGET.md):
//   S1: raw string.Create<TState> + SpanAction<char,string[]> delegate path
//   S2: Microsoft.Extensions.Primitives StringValues.ToString()
//   S3: Tomlyn 2.10.1 syntax parser
// Humanizer probes live in src/apps/humprobe (the Humanizer BCL surface
// differs from the Tomlyn one; keeping them apart lets each app validate
// independently).
// Each section runs under try/catch so one failure does not mask the rest.
using System;

static class Probe
{
    static void Main(string[] args)
    {
        Console.WriteLine("thirdprobe start");
        Run("S1-string-create", S1);
        Run("S2-stringvalues", S2);
        Run("S2b-instance-delegate", S2b);
        Run("S2c-span-chain", S2c);
        Run("S2d-span-strings", S2d);
        Run("S2e-bcl-isolation", S2e);
        Run("S3-toml-syntax", S3);
        Run("S3x-value-getters", S3x);
        Run("S3y-bcl-mimics", S3y);
        Console.WriteLine("thirdprobe done");
    }

    static void Run(string name, Action section)
    {
        Console.WriteLine("== " + name);
        try
        {
            section();
            Console.WriteLine("== " + name + " OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine("== " + name + " EXC: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    static void S1()
    {
        string[] parts = new string[] { "alpha", "beta", "gamma" };
        int len = 0;
        for (int i = 0; i < parts.Length; i++) len += parts[i].Length;
        len += parts.Length - 1;

        string joined = string.Create<string[]>(len, parts, static (Span<char> span, string[] arr) =>
        {
            int pos = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                if (i != 0) span[pos++] = ',';
                string p = arr[i];
                for (int j = 0; j < p.Length; j++) span[pos++] = p[j];
            }
        });

        Console.WriteLine("S1: joined=" + joined);
        Console.WriteLine("S1: match=" + (joined == "alpha,beta,gamma"));
    }

    static void S2()
    {
        var values = new Microsoft.Extensions.Primitives.StringValues(new string[] { "alpha", "beta", "gamma" });
        Console.WriteLine("S2: count=" + values.Count);
        string s = values.ToString();
        Console.WriteLine("S2: joined=" + s);
        Console.WriteLine("S2: len=" + s.Length);
        Console.WriteLine("S2: match=" + (s == "alpha,beta,gamma"));
    }

    sealed class Joiner
    {
        public void Fill(Span<char> span, string[] arr)
        {
            int pos = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                if (i != 0) span[pos++] = ',';
                string p = arr[i];
                for (int j = 0; j < p.Length; j++) span[pos++] = p[j];
            }
        }
    }

    static void S2b()
    {
        string[] parts = new string[] { "one", "two", "three" };
        int len = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (i != 0) len++;
            len += parts[i].Length;
        }
        var j = new Joiner();
        string s = string.Create<string[]>(len, parts, j.Fill);
        Console.WriteLine("S2b: joined=" + s);
        Console.WriteLine("S2b: len=" + s.Length);
        Console.WriteLine("S2b: match=" + (s == "one,two,three"));
    }

    static void S2c()
    {
        string s = "alpha";
        var ro = System.MemoryExtensions.AsSpan(s);
        Console.WriteLine("S2c: roLen=" + ro.Length);
        char[] buf = new char[5];
        Span<char> dst = System.MemoryExtensions.AsSpan(buf);
        Console.WriteLine("S2c: dstLen=" + dst.Length);
        ro.CopyTo(dst);
        Console.WriteLine("S2c: b0=" + (int)buf[0] + " b4=" + (int)buf[4]);

        // Slice path (same shape as StringValues' lambda)
        Span<char> tail = dst.Slice(2);
        Console.WriteLine("S2c: tailLen=" + tail.Length);
    }

    static void S2d()
    {
        // The /strings route's exact operations (libapi S2 parity check):
        // StringSegment routes starts/equals/indexof through korlib span
        // helpers; this isolates span-vs-span ops from the web stack.
        var segment = new Microsoft.Extensions.Primitives.StringSegment("hello world", 0, 5);
        Console.WriteLine("S2d: seg=" + segment.Value);
        bool starts = segment.StartsWith("hello", StringComparison.Ordinal);
        bool equals = segment.Equals(new Microsoft.Extensions.Primitives.StringSegment("hello"), StringComparison.Ordinal);
        int at = segment.IndexOf('l');
        Console.WriteLine("S2d: starts=" + starts + " equals=" + equals + " indexof=" + at);

        // Raw korlib span ops (no Primitives involved).
        string text = "hello world";
        var a = text.AsSpan(0, 5);
        var v = "hello".AsSpan();
        Console.WriteLine("S2d: aLen=" + a.Length + " vLen=" + v.Length);
        Console.WriteLine("S2d: cs0=" + (int)a[0] + " cv0=" + (int)v[0]);
        Console.WriteLine("S2d: start=" + a.StartsWith(v, StringComparison.Ordinal));
        Console.WriteLine("S2d: eq=" + a.Equals(v, StringComparison.Ordinal));
        Console.WriteLine("S2d: ix=" + a.IndexOf('l'));
    }

    static void S3()
    {
        // Syntax path: parse + structure counts for the TOML constructs,
        // then typed value access on the top-level key/values. (SyntaxNode
        // ToString()/WriteTo traversal is deliberately avoided - the node
        // writer's virtual dispatch is a separate, not-yet-supported shape.)
        Check("S3a", "a = 1\n");
        Check("S3b", "title = \"hello\"\n");
        Check("S3c", "[server]\nport = 8080\n");
        Check("S3d", "title = \"hello\"\n[server]\nport = 8080\n");

        var sdoc = Tomlyn.Parsing.SyntaxParser.Parse("title = \"hello\"\nnum = 42\n", "probe", false);
        foreach (var kv in sdoc.KeyValues)
        {
            if (kv.Value is Tomlyn.Syntax.StringValueSyntax sv)
                Console.WriteLine("S3: stringvalue=" + sv.Value);
            else if (kv.Value is Tomlyn.Syntax.IntegerValueSyntax iv)
                Console.WriteLine("S3: intvalue=" + iv.Value);
        }
    }

    static void Check(string label, string text)
    {
        var d = Tomlyn.Parsing.SyntaxParser.Parse(text, label, false);
        Console.WriteLine("S3." + label + ": kv=" + d.KeyValues.ChildrenCount + " tables=" + d.Tables.ChildrenCount);
    }

    // BCL isolation: span.ToString(), StringBuilder round-trip and numeric
    // parse paths (all exercised by Tomlyn's lexer/parser leaf-value code).
    static void S2e()
    {
        System.ReadOnlySpan<char> sp = "hello world".AsSpan(0, 5);
        string s1 = sp.ToString();
        Console.WriteLine("S2e: spanstr-null=" + (s1 == null));
        if (s1 != null)
        {
            Console.WriteLine("S2e: spanstr-len=" + s1.Length);
            Console.WriteLine("S2e: spanstr=" + s1);
        }

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append('a');
        sb.Append("bc");
        string s2 = sb.ToString();
        Console.WriteLine("S2e: sb=" + s2);
        Console.WriteLine("S2e: sbidx0=" + (int)sb[0]);
        Console.WriteLine("S2e: sbidx1=" + (int)sb[1]);
    }

    // Deep diagnostics for the typed-value-getter boundary (docs/NUGET.md):
    // fresh ctor node vs parsed node, plus plain BCL long/int concatenation
    // so a primitive ToString fault is distinguishable from a Tomlyn fault.
    static void S3x()
    {
        var fresh = new Tomlyn.Syntax.StringValueSyntax("hello");
        DumpString("S3x-fresh", fresh.Value);

        var sdoc = Tomlyn.Parsing.SyntaxParser.Parse("title = \"hello\"\nnum = 3231\n", "probe", false);
        foreach (var kv in sdoc.KeyValues)
        {
            if (kv.Value is Tomlyn.Syntax.StringValueSyntax sv)
            {
                DumpString("S3x-parsed-sv", sv.Value);
                var tok = sv.Token;
                Console.WriteLine("S3x-parsed-tok-null=" + (tok == null));
                if (tok != null)
                    DumpString("S3x-parsed-tok-text", tok.Text);
            }
            else if (kv.Value is Tomlyn.Syntax.IntegerValueSyntax iv)
            {
                long lv = iv.Value;
                Console.WriteLine("S3x-parsed-iv-long=" + lv);
                int ic = (int)lv;
                Console.WriteLine("S3x-parsed-iv-int=" + ic);
            }
            else
            {
                Console.WriteLine("S3x-other-node=" + kv.Value.GetType().Name);
            }
        }

        // Plain BCL isolation (no Tomlyn types involved).
        long pureLong = 42L;
        int pureInt = 4242;
        Console.WriteLine("S3x-pure-long=" + pureLong);
        Console.WriteLine("S3x-pure-int=" + pureInt);

        // Value-kind sweep: which typed getters work? (each step bracketed)
        Sweep("int", "num = 3231\n");
        Sweep("int42", "num = 42\n");
        Sweep("intneg", "num = -5\n");
        Sweep("float", "fl = 3.25\n");
        Sweep("float-e", "fl = 1e3\n");
        Sweep("bool", "ok = true\n");
        Sweep("date", "d = 1979-05-27\n");
    }

    // Mimic Tomlyn's TryParseDecimalInt64 over an identical StringBuilder
    // fill pattern, plus StringBuilder.CopyTo(span) and Nullable checks.
    static void S3y()
    {
        var tb = new System.Text.StringBuilder();
        tb.Append('3'); tb.Append('2'); tb.Append('3'); tb.Append('1');
        Console.WriteLine("S3y: len=" + tb.Length + " t=" + tb.ToString());
        for (int i = 0; i < tb.Length; i++)
        {
            char dc = tb[i];
            int digit = dc - '0';
            Console.WriteLine("S3y: i=" + i + " dc=" + (int)dc + " dg=" + digit + " bad=" + ((uint)digit > 9));
        }
        long val = 0; bool ok = true;
        for (int i = 0; i < tb.Length; i++)
        {
            var dc = tb[i];
            var digit = dc - '0';
            if ((uint)digit > 9) { ok = false; break; }
            val = (val * 10) + digit;
        }
        Console.WriteLine("S3y: parsed=" + val + " ok=" + ok);

        // Exact TryParseDecimalInt64 mimic (incl. the ulong overflow check
        // and an out-param), then the hasLeadingZero second-branch shape.
        bool ok2 = TryParseDec64(tb, out long val2);
        Console.WriteLine("S3y: dec64ok=" + ok2 + " dec64v=" + val2);
        bool hasZero = false;
        Console.WriteLine("S3y: branch1=" + (hasZero && val2 != 0));
        bool hasZero2 = true;
        Console.WriteLine("S3y: branch2=" + (hasZero2 && val2 != 0));

        // CopyTo variants.
        char[] arr = new char[2];
        tb.CopyTo(0, arr, 0, 2);
        Console.WriteLine("S3y: copyarr=" + (int)arr[0] + "," + (int)arr[1]);

        System.Span<char> buf0 = stackalloc char[2];
        buf0[0] = 'q';
        Console.WriteLine("S3y: stackspan0=" + (int)buf0[0]);

        try
        {
            System.Span<char> buf = stackalloc char[2];
            tb.CopyTo(0, buf, 2);
            Console.WriteLine("S3y: copyto=" + (int)buf[0] + "," + (int)buf[1]);
        }
        catch (Exception ex)
        {
            string msg = "?";
            try { msg = ex.Message; } catch { }
            Console.WriteLine("S3y: copyto-exc=[" + msg + "]");
        }

        int? nullable = 5;
        Console.WriteLine("S3y: null?" + (nullable != null) + " v=" + nullable.GetValueOrDefault());
    }

    static bool TryParseDec64(System.Text.StringBuilder tb, out long value)
    {
        value = 0;
        var length = tb.Length;
        if (length == 0) return false;
        var index = 0;
        var negative = false;
        var first = tb[0];
        if (first == '+' || first == '-')
        {
            negative = first == '-';
            index = 1;
            if (index >= length) return false;
        }
        ulong accumulator = 0;
        for (; index < length; index++)
        {
            var digitChar = tb[index];
            var digit = digitChar - '0';
            if ((uint)digit > 9) return false;
            var digitValue = (ulong)digit;
            if (accumulator > (ulong.MaxValue - digitValue) / 10) return false;
            accumulator = (accumulator * 10) + digitValue;
        }
        if (negative) { value = unchecked(-(long)accumulator); return true; }
        value = unchecked((long)accumulator);
        return true;
    }

    static void Sweep(string label, string toml)
    {
        Console.WriteLine("S3x-sw-" + label + "-start");
        Tomlyn.Syntax.DocumentSyntax d = null;
        try
        {
            d = Tomlyn.Parsing.SyntaxParser.Parse(toml, "probe", false);
        }
        catch (Exception ex)
        {
            string msg = "?";
            try { msg = ex.Message; } catch { }
            string full = "?";
            try { full = ex.ToString(); } catch { }
            Console.WriteLine("S3x-sw-" + label + "-parse-exc=[" + msg + "] full=[" + full + "]");
        }
        if (d == null)
        {
            Console.WriteLine("S3x-sw-" + label + "-nodoc");
        }
        else
        {
            try
            {
                Console.WriteLine("S3x-sw-" + label + "-diag=" + (d.Diagnostics == null ? -1 : d.Diagnostics.Count));
                if (d.Diagnostics != null)
                {
                    foreach (var msg in d.Diagnostics)
                    {
                        string sm = "?";
                        try { sm = msg.ToString(); } catch { }
                        Console.WriteLine("S3x-sw-" + label + "-dmsg=[" + sm + "]");
                    }
                }
            }
            catch (Exception ex)
            {
                string msg = "?";
                try { msg = ex.Message; } catch { }
                Console.WriteLine("S3x-sw-" + label + "-diagerr=[" + msg + "]");
            }
            foreach (var kv in d.KeyValues)
            {
                var v = kv.Value;
                try
                {
                    if (v is Tomlyn.Syntax.FloatValueSyntax fv)
                        Console.WriteLine("S3x-sw-" + label + "-float=" + fv.Value);
                    else if (v is Tomlyn.Syntax.BooleanValueSyntax bv)
                        Console.WriteLine("S3x-sw-" + label + "-bool=" + bv.Value);
                    else if (v is Tomlyn.Syntax.IntegerValueSyntax iv2)
                    {
                        var itok = iv2.Token;
                        Console.WriteLine("S3x-sw-" + label + "-int=" + iv2.Value + " tok=" + (itok == null ? "null" : itok.Text));
                    }
                    else if (v is Tomlyn.Syntax.DateTimeValueSyntax dv)
                        Console.WriteLine("S3x-sw-" + label + "-date-ok");
                    else
                        Console.WriteLine("S3x-sw-" + label + "-other");
                }
                catch (Exception ex)
                {
                    string msg = "?";
                    try { msg = ex.Message; } catch { }
                    Console.WriteLine("S3x-sw-" + label + "-getter-exc=[" + msg + "]");
                }
            }
        }
        Console.WriteLine("S3x-sw-" + label + "-end");
    }

    static void DumpString(string label, string s)
    {
        Console.WriteLine(label + "-null=" + (s == null));
        if (s != null)
        {
            Console.WriteLine(label + "-len=" + s.Length);
            Console.WriteLine(label + "-c0=" + (s.Length > 0 ? (int)s[0] : -1));
            Console.WriteLine(label + "-val=" + s);
        }
    }
}
