// Token dumper: identify DDK MethodDef RIDs from the JIT error logs.
// usage: tokdump <assembly.dll> <fromHex> <toHex> [ilForRid]
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

class Program
{
    static void Main(string[] args)
    {
        using var fs = File.OpenRead(args[0]);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        int from = Convert.ToInt32(args[1], 16);
        int to = Convert.ToInt32(args[2], 16);
        int ilFor = args.Length > 3 ? Convert.ToInt32(args[3], 16) : -1;

        int rid = 0;
        foreach (var h in md.MethodDefinitions)
        {
            rid++;
            if (rid < from || rid > to) continue;
            var m = md.GetMethodDefinition(h);
            var t = md.GetTypeDefinition(m.GetDeclaringType());
            Console.WriteLine($"0x{rid:X4} {md.GetString(t.Namespace)}.{md.GetString(t.Name)}::{md.GetString(m.Name)} RVA=0x{m.RelativeVirtualAddress:X}");
            if (rid == ilFor)
            {
                var body = pe.GetMethodBody(m.RelativeVirtualAddress);
                var il = body.GetILBytes();
                Console.Write("     IL:");
                for (int i = 0; i < il.Length; i++)
                    Console.Write($" {il[i]:X2}");
                Console.WriteLine();
            }
        }
    }
}
