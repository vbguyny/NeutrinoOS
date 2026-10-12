// Scratch IL/token inspection tool for NeutrinoOS bring-up debugging.
// usage: iltool <assembly.dll> ms <methodSpecRowHex>   (decode MethodSpec)
//        iltool <assembly.dll> il <methodDefRowHex>    (dump IL, tokens resolved)
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length < 3)
{
    Console.WriteLine("usage: iltool <dll> ms <methodSpecRowHex> | il <methodDefRowHex>");
    return 2;
}

string path = args[0];
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();

if (args[1] == "ms")
{
    int row = int.Parse(args[2], System.Globalization.NumberStyles.HexNumber);
    var ms = md.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(row));
    Console.WriteLine("MethodSpec!" + row.ToString("X") + " -> " + Describe(ms.Method));
    byte[] sigBytes = md.GetBlobBytes(ms.Signature);
    Console.WriteLine("sig len=" + sigBytes.Length + " ["
        + string.Join(" ", sigBytes.Select(b => b.ToString("X2"))) + "]");
    var br = md.GetBlobReader(ms.Signature);
    int n = br.ReadCompressedInteger();
    Console.WriteLine("typeArgs=" + n);
    for (int i = 0; i < n && br.RemainingBytes > 0; i++)
    {
        byte code = br.ReadByte();
        switch (code)
        {
            case 0x11:
            case 0x12:
                Console.WriteLine("  [" + i + "] " + Describe(br.ReadTypeHandle()));
                break;
            case 0x13:
                Console.WriteLine("  [" + i + "] VAR!" + br.ReadCompressedInteger());
                break;
            case 0x1E:
                Console.WriteLine("  [" + i + "] MVAR!" + br.ReadCompressedInteger());
                break;
            default:
                Console.WriteLine("  [" + i + "] (elementtype 0x" + code.ToString("X2") + ", unparsed)");
                break;
        }
    }
    return 0;
}

if (args[1] == "il")
{
    int row = int.Parse(args[2], System.Globalization.NumberStyles.HexNumber);
    var m = md.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(row));
    Console.WriteLine(Describe(MetadataTokens.EntityHandle(0x06000000 | row)));
    var body = pe.GetMethodBody(m.RelativeVirtualAddress);
    byte[] il = body.GetILBytes();
    Console.WriteLine("ilSize=" + il.Length);
    int p = 0;
    while (p < il.Length)
    {
        int off = p;
        byte op = il[p++];
        int operand = 0;
        bool hasTok = false;
        switch (op)
        {
            case 0x28: case 0x6F: case 0x73: case 0xD0: case 0x74: case 0x75:
            case 0x8C: case 0x7C: case 0x7D: case 0x8D: case 0xA3: case 0xA4:
                operand = BitConverter.ToInt32(il, p); p += 4; hasTok = true; break;
            case 0xFE:
                {
                    byte op2 = il[p++];
                    if (op2 == 0x06 || op2 == 0x07)
                    {
                        operand = BitConverter.ToInt32(il, p); p += 4; hasTok = true;
                    }
                    break;
                }
            case 0x45:
                {
                    int cnt = BitConverter.ToInt32(il, p);
                    p += 4 + cnt * 4;
                    break;
                }
            case 0x20: case 0x22: case 0x38:
                p += 4; break;
            case 0x21: case 0x23: case 0x27:
                p += 8; break;
            case 0x2B: case 0x39: case 0x2C: case 0x3A: case 0x2D: case 0x3B:
            case 0x2E: case 0x3C: case 0x2F: case 0x3D: case 0x30: case 0x3E:
            case 0x31: case 0x3F: case 0x32: case 0x40: case 0x33: case 0x41:
            case 0x0E: case 0x10: case 0x11: case 0x12: case 0x13: case 0x1F:
                p += 1; break;
        }
        string tk = "";
        if (hasTok)
            tk = " tok=" + operand.ToString("X8") + " " + Describe(MetadataTokens.EntityHandle(operand));
        Console.WriteLine(off.ToString("X4") + ": " + op.ToString("X2") + tk);
    }
    return 0;
}

Console.WriteLine("unknown mode " + args[1]);
return 2;

string Describe(EntityHandle h)
{
    switch (h.Kind)
    {
        case HandleKind.MethodDefinition:
            {
                var m = md.GetMethodDefinition((MethodDefinitionHandle)h);
                var t = md.GetTypeDefinition(m.GetDeclaringType());
                return TypeName(t) + "." + md.GetString(m.Name);
            }
        case HandleKind.MemberReference:
            {
                var mr = md.GetMemberReference((MemberReferenceHandle)h);
                return Describe(mr.Parent) + "::" + md.GetString(mr.Name);
            }
        case HandleKind.MethodSpecification:
            {
                int r = MetadataTokens.GetRowNumber(h);
                return "MethodSpec!" + r.ToString("X") + " -> "
                    + Describe(md.GetMethodSpecification((MethodSpecificationHandle)h).Method);
            }
        case HandleKind.TypeReference:
            {
                var tr = md.GetTypeReference((TypeReferenceHandle)h);
                return (tr.Namespace.IsNil ? "" : md.GetString(tr.Namespace) + ".") + md.GetString(tr.Name);
            }
        case HandleKind.TypeDefinition:
            return TypeName(md.GetTypeDefinition((TypeDefinitionHandle)h));
        case HandleKind.TypeSpecification:
            return "TypeSpec!" + MetadataTokens.GetRowNumber(h).ToString("X");
        case HandleKind.FieldDefinition:
            {
                var f = md.GetFieldDefinition((FieldDefinitionHandle)h);
                var t = md.GetTypeDefinition(f.GetDeclaringType());
                return TypeName(t) + "." + md.GetString(f.Name);
            }
        default:
            return h.Kind + " token=" + MetadataTokens.GetToken(h).ToString("X8");
    }
}

string TypeName(TypeDefinition t)
{
    string ns = t.Namespace.IsNil ? "" : md.GetString(t.Namespace);
    return ns.Length == 0 ? md.GetString(t.Name) : ns + "." + md.GetString(t.Name);
}
