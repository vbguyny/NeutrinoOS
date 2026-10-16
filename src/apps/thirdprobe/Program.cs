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
        Run("S3-toml-syntax", S3);
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
}
