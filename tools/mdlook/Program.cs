// Token resolver: prints MethodDef name for a token in a .NET assembly.
// usage: dotnet run -- <assembly.dll> <hex-token-without-0x> [more tokens...]
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length < 2)
{
    Console.WriteLine("usage: mdlook <assembly.dll> <hexToken> [hexToken...]");
    return 1;
}

string path = args[0];
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();

for (int i = 1; i < args.Length; i++)
{
    string tokStr = args[i].Replace("0x", "", StringComparison.OrdinalIgnoreCase);
    int row;
    if (tokStr.Length > 6)
    {
        // full token: 0x06xxxxxx
        row = int.Parse(tokStr[^6..], System.Globalization.NumberStyles.HexNumber);
    }
    else
    {
        row = int.Parse(tokStr, System.Globalization.NumberStyles.HexNumber);
    }
    try
    {
        var handle = MetadataTokens.MethodDefinitionHandle(row);
        var m = md.GetMethodDefinition(handle);
        string name = md.GetString(m.Name);
        var t = md.GetTypeDefinition(m.GetDeclaringType());
        string type = md.GetString(t.Name);
        string ns = t.Namespace.IsNil ? "" : md.GetString(t.Namespace);
        Console.WriteLine($"{tokStr} -> {ns}.{type}.{name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{tokStr} -> error: {ex.Message}");
    }
}
return 0;
