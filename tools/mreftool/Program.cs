using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Resolves MemberRef rows (including TypeSpec parents) so the device JIT's
// unresolved-token log lines can be turned into names.
//   mref <dll> [substring] [--fields]
internal static class Program
{
    private static MetadataReader Md = null!;
    private static PEReader Pea = null!;

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: mref <dll> [substring] [--fields]");
            return 2;
        }

        using var fs = File.OpenRead(args[0]);
        using var pe = new PEReader(fs);
        Md = pe.GetMetadataReader();
        Pea = pe;

        string? filter = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        bool fieldsOnly = args.Contains("--fields");
        bool rawMode = filter != null && filter.StartsWith("@");
        int rawRow = rawMode ? Convert.ToInt32(filter.Substring(1), 16) : 0;

        if (args.Length >= 3 && args[1] == "--typedefs")
        {
            foreach (var td in Md.TypeDefinitions)
            {
                string n = TypeDefName(td);
                if (n.Contains(args[2], StringComparison.Ordinal)) Console.WriteLine(n);
            }
            return 0;
        }

        if (args.Length >= 3 && args[1] == "--typeref")
        {
            int tr = Convert.ToInt32(args[2], 16);
            Console.WriteLine($"TypeRef[{tr}] = " + TypeRefName(MetadataTokens.TypeReferenceHandle(tr)));
            return 0;
        }

        // --methodspec <hex row>: decode a MethodSpec row (MethodDefOrRef coded
        // index + instantiation blob) so `call 0x2Bxxxxxx` log lines can be read.
        if (args.Length >= 3 && args[1] == "--methodspec")
        {
            int mrow = Convert.ToInt32(args[2], 16);
            var msh = MetadataTokens.MethodSpecificationHandle(mrow);
            var spec = Md.GetMethodSpecification(msh);
            Console.WriteLine($"MethodSpec 0x2B{mrow:X6}");
            Console.WriteLine("  method => " + DescribeToken(DescribeMethodDefOrRef(spec.Method)));
            var blob = Md.GetBlobBytes(spec.Signature);
            Console.WriteLine("  instantiation blob: " + Convert.ToHexString(blob));
            try
            {
                var br = Md.GetBlobReader(spec.Signature);
                int n = br.ReadCompressedInteger();
                var parts = new List<string>();
                for (int i = 0; i < n && br.RemainingBytes > 0; i++) parts.Add(ReadType(ref br));
                Console.WriteLine($"  args({n}): {string.Join(", ", parts)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  args? " + ex.GetType().Name);
            }
            return 0;
        }

        // --methods <typeDefSubstring>: list every MethodDef of the matching
        // TypeDefs with its row and signature blob, so a MemberRef that fails
        // to match can be compared against the real definition.
        if (args.Length >= 3 && args[1] == "--methods")
        {
            foreach (var tdh in Md.TypeDefinitions)
            {
                string tn = TypeDefName(tdh);
                if (!tn.Contains(args[2], StringComparison.Ordinal)) continue;
                var td = Md.GetTypeDefinition(tdh);
                Console.WriteLine($"== {tn} (TypeDef 0x02{MetadataTokens.GetRowNumber(tdh):X6})");
                foreach (var mh2 in td.GetMethods())
                {
                    var m2 = Md.GetMethodDefinition(mh2);
                    Console.WriteLine($"   0x06{MetadataTokens.GetRowNumber(mh2):X6} {Md.GetString(m2.Name)}  sig={Convert.ToHexString(Md.GetBlobBytes(m2.Signature))}  implAttrs=0x{(int)m2.ImplAttributes:X} attrs=0x{(int)m2.Attributes:X}");
                }
            }
            return 0;
        }

        // --methoddef <hex row>: identify a MethodDef and dump its IL so a
        // device-side "unknown opcode at IL offset N" log line can be mapped
        // back to the exact instruction and its token.
        if (args.Length >= 3 && args[1] == "--methoddef")
        {
            int mrow = Convert.ToInt32(args[2], 16);
            var mh = MetadataTokens.MethodDefinitionHandle(mrow);
            var md = Md.GetMethodDefinition(mh);
            Console.WriteLine($"MethodDef 0x06{mrow:X6} = {TypeDefName(md.GetDeclaringType())}::{Md.GetString(md.Name)}");
            Console.WriteLine($"  sig blob: {Convert.ToHexString(Md.GetBlobBytes(md.Signature))}");
            if (md.RelativeVirtualAddress == 0)
            {
                Console.WriteLine("  (no body)");
                return 0;
            }
            var body = Pea.GetMethodBody(md.RelativeVirtualAddress);
            byte[] il = body.GetILBytes()!;
            Console.WriteLine($"  ilLen={il.Length} maxStack={body.MaxStack} localSig={body.LocalSignature.IsNil} IL:\n  " + Convert.ToHexString(il));
            // Reference constants at a given offset, if one was supplied.
            if (args.Length >= 4 && int.TryParse(args[3], out int off) && off >= 0 && off + 5 <= il.Length)
            {
                byte op = il[off];
                uint tok = (uint)(il[off + 1] | (il[off + 2] << 8) | (il[off + 3] << 16) | (il[off + 4] << 24));
                Console.WriteLine($"  IL[{off}] opcode=0x{op:X2} token=0x{tok:X8}");
                Console.WriteLine("  token => " + DescribeToken(tok));
            }
            return 0;
        }

        // --callers <hexrow>: scan every method body for call/callvirt/newobj
        // instructions referencing the given MemberRef row. Used to find the
        // korlib source sites that emit a BCL-only reference.
        if (args.Length >= 3 && args[1] == "--callers")
        {
            uint target = 0x0A000000u | (uint)Convert.ToInt32(args[2], 16);
            byte[] pat = { (byte)(target & 0xFF), (byte)((target >> 8) & 0xFF), (byte)((target >> 16) & 0xFF), (byte)((target >> 24) & 0xFF) };
            int hits = 0;
            foreach (var mh in Md.MethodDefinitions)
            {
                var md = Md.GetMethodDefinition(mh);
                if (md.RelativeVirtualAddress == 0) continue;
                byte[] il;
                try { il = Pea.GetMethodBody(md.RelativeVirtualAddress).GetILBytes(); }
                catch (Exception) { continue; }
                if (il == null || il.Length < 5) continue;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] != pat[0] || il[i + 1] != pat[1] || il[i + 2] != pat[2] || il[i + 3] != pat[3]) continue;
                    if (i == 0) continue;
                    byte op = il[i - 1];
                    if (op != 0x28 && op != 0x6F && op != 0x73 && op != 0xD0 && op != 0x7E && op != 0x80)
                        continue;
                    hits++;
                    Console.WriteLine($"0x06{MetadataTokens.GetRowNumber(mh):X6} {TypeDefName(md.GetDeclaringType())}::{Md.GetString(md.Name)}  il@0x{i - 1:X} op=0x{op:X2}");
                }
            }
            Console.WriteLine($"-- {hits} call site(s) for 0x{target:X8} --");
            return 0;
        }

        int row = 0;
        foreach (var h in Md.MemberReferences)
        {
            row++;
            var mr = Md.GetMemberReference(h);
            if (fieldsOnly && mr.GetKind() != MemberReferenceKind.Field) continue;

            if (rawMode)
            {
                if (row != rawRow) continue;
                Console.WriteLine($"row 0x0A{row:X6} name={Md.GetString(mr.Name)} kind={mr.GetKind()}");
                Console.WriteLine($"  parent kind={mr.Parent.Kind}");
                if (mr.Parent.Kind == HandleKind.TypeSpecification)
                {
                    var spec = Md.GetTypeSpecification((TypeSpecificationHandle)mr.Parent);
                    Console.WriteLine("  parent blob: " + Convert.ToHexString(Md.GetBlobBytes(spec.Signature)));
                }
                var sb = Md.GetBlobReader(mr.Signature);
                Console.WriteLine("  sig blob:    " + Convert.ToHexString(Md.GetBlobBytes(mr.Signature)));
                return 0;
            }

            string full;
            string sig;
            try { full = ParentName(mr.Parent) + "::" + Md.GetString(mr.Name); }
            catch (Exception) { full = "<parent?>::" + Md.GetString(mr.Name); }
            if (filter != null && !full.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            try { sig = DescribeSignature(mr); }
            catch (Exception ex) { sig = "sig? " + ex.GetType().Name; }

            Console.WriteLine($"0x0A{row:X6} {full}   [{sig}]");
        }
        return 0;
    }

    private static uint DescribeMethodDefOrRef(EntityHandle h) => h.Kind switch
    {
        HandleKind.MethodDefinition => 0x06000000u | (uint)MetadataTokens.GetRowNumber(h),
        HandleKind.MemberReference => 0x0A000000u | (uint)MetadataTokens.GetRowNumber(h),
        _ => 0xFFFFFFFFu,
    };

    // Describe an IL metadata token (e.g. one found at a call site).
    private static string DescribeToken(uint tok)
    {
        uint table = tok >> 24;
        int row = (int)(tok & 0x00FFFFFF);
        try
        {
            switch (table)
            {
                case 0x0A:
                {
                    var mr = Md.GetMemberReference(MetadataTokens.MemberReferenceHandle(row));
                    return ParentName(mr.Parent) + "::" + Md.GetString(mr.Name) +
                           "   [" + DescribeSignature(mr) + "]";
                }
                case 0x06:
                {
                    var md2 = Md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(row));
                    return TypeDefName(md2.GetDeclaringType()) + "::" + Md.GetString(md2.Name);
                }
                case 0x01:
                    return "TypeRef " + TypeRefName(MetadataTokens.TypeReferenceHandle(row));
                case 0x02:
                    return "TypeDef " + TypeDefName(MetadataTokens.TypeDefinitionHandle(row));
                case 0x04:
                {
                    var fd = Md.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(row));
                    return "FieldDef " + TypeDefName(fd.GetDeclaringType()) + "::" + Md.GetString(fd.Name);
                }
                case 0x70:
                    return "user string " + Md.GetUserString(MetadataTokens.UserStringHandle(row));
                default:
                    return $"table 0x{table:X2} row {row}";
            }
        }
        catch (Exception ex)
        {
            return "token? " + ex.GetType().Name;
        }
    }

    private static string ParentName(EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => TypeRefName((TypeReferenceHandle)parent),
        HandleKind.TypeDefinition => TypeDefName((TypeDefinitionHandle)parent),
        HandleKind.TypeSpecification => TypeSpecName((TypeSpecificationHandle)parent),
        _ => parent.Kind.ToString(),
    };

    private static string TypeRefName(TypeReferenceHandle h)
    {
        var tr = Md.GetTypeReference(h);
        string n = Md.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return TypeRefName((TypeReferenceHandle)tr.ResolutionScope) + "+" + n;
        string ns = Md.GetString(tr.Namespace);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }

    private static string TypeDefName(TypeDefinitionHandle h)
    {
        var td = Md.GetTypeDefinition(h);
        string n = Md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) return TypeDefName(decl) + "+" + n;
        string ns = Md.GetString(td.Namespace);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }

    private static int _specDepth;
    private static string TypeSpecName(TypeSpecificationHandle h)
    {
        if (_specDepth > 8) return "typespec!deep";
        _specDepth++;
        try
        {
            var spec = Md.GetTypeSpecification(h);
            var blob = Md.GetBlobReader(spec.Signature);
            return ReadType(ref blob);
        }
        catch (Exception)
        {
            return "typespec!" + MetadataTokens.GetRowNumber(h).ToString("X");
        }
        finally
        {
            _specDepth--;
        }
    }

    private static string DescribeSignature(MemberReference mr)
    {
        try
        {
            var blob = Md.GetBlobReader(mr.Signature);
            if (mr.GetKind() == MemberReferenceKind.Field)
                return ReadType(ref blob);

            byte header = blob.ReadByte();
            if ((header & 0x10) != 0) blob.ReadCompressedInteger();   // generic param count
            int count = blob.ReadCompressedInteger();
            var parts = new List<string>();
            for (int i = 0; i < count && blob.RemainingBytes > 0; i++) parts.Add(ReadType(ref blob));
            string ret = blob.RemainingBytes > 0 ? ReadType(ref blob) : "?";
            return $"({string.Join(", ", parts)}) -> {ret}";
        }
        catch (Exception ex)
        {
            return "sig? " + ex.GetType().Name;
        }
    }

    // Minimal ECMA-335 type-blob reader: enough for member refs and TypeSpecs.
    private static string ReadType(ref BlobReader b) => ReadType(ref b, 0);

    private static string ReadType(ref BlobReader b, int depth)
    {
        if (depth > 24 || b.RemainingBytes <= 0) return "?";
        byte code = b.ReadByte();
        switch (code)
        {
            case 0x01: return "void";
            case 0x02: return "bool";
            case 0x03: return "char";
            case 0x04: return "sbyte";
            case 0x05: return "byte";
            case 0x06: return "short";
            case 0x07: return "ushort";
            case 0x08: return "int";
            case 0x09: return "uint";
            case 0x0A: return "long";
            case 0x0B: return "ulong";
            case 0x0C: return "float";
            case 0x0D: return "double";
            case 0x0E: return "string";
            case 0x16: return "typedref";
            case 0x18: return "nint";
            case 0x19: return "nuint";
            case 0x1C: return "object";
            case 0x0F: return ReadType(ref b, depth + 1) + "*";
            case 0x10: return ReadType(ref b, depth + 1) + "&";
            case 0x1D: return ReadType(ref b, depth + 1) + "[]";
            case 0x11:
            case 0x12:
                return HandleName(b.ReadTypeHandle());
            case 0x13:
                return "!" + b.ReadCompressedInteger();
            case 0x1E:
                return "!!" + b.ReadCompressedInteger();
            case 0x1F:
            case 0x20:
                b.ReadTypeHandle();      // modifier
                return ReadType(ref b, depth + 1);
            case 0x15:
                {
                    string generic = HandleName(b.ReadTypeHandle());
                    int n = b.ReadCompressedInteger();
                    var args = new List<string>();
                    for (int i = 0; i < n && b.RemainingBytes > 0; i++) args.Add(ReadType(ref b, depth + 1));
                    return generic + "<" + string.Join(", ", args) + ">";
                }
            case 0x14:
                {
                    string elem = ReadType(ref b, depth + 1);
                    int rank = b.ReadCompressedInteger();
                    int sizes = b.ReadCompressedInteger();
                    for (int i = 0; i < sizes; i++) b.ReadCompressedInteger();
                    int bounds = b.ReadCompressedInteger();
                    for (int i = 0; i < bounds; i++) b.ReadCompressedInteger();
                    return elem + "[" + new string(',', Math.Max(0, rank - 1)) + "]";
                }
            default:
                return "et0x" + code.ToString("X2");
        }
    }

    private static string HandleName(EntityHandle h) => h.Kind switch
    {
        HandleKind.TypeReference => TypeRefName((TypeReferenceHandle)h),
        HandleKind.TypeDefinition => TypeDefName((TypeDefinitionHandle)h),
        HandleKind.TypeSpecification => TypeSpecName((TypeSpecificationHandle)h),
        _ => h.Kind.ToString(),
    };
}
