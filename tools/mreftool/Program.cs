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
