#!/bin/bash
# Dump MethodDef token -> name for a managed DLL (host-side JIT crash triage).
# Usage: bash p8-tokdump.sh <dll> [token ...]   (tokens like 0x0600037C)
set -uo pipefail
DLL="${1:?usage: p8-tokdump.sh <dll> [token ...]}"
shift || true
DIR=/root/tokdump
if [ ! -f "$DIR/tokdump.csproj" ]; then
  mkdir -p "$DIR"
  cat > "$DIR/tokdump.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
</Project>
EOF
  cat > "$DIR/Program.cs" <<'EOF'
using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

class Program
{
    static void Main(string[] args)
    {
        string dll = args[0];
        var wanted = new HashSet<int>();
        for (int i = 1; i < args.Length; i++)
            wanted.Add(Convert.ToInt32(args[i].Replace("0x", ""), 16));

        using var fs = System.IO.File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        foreach (var h in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(h);
            int tok = MetadataTokens.GetToken(h);
            if (wanted.Count == 0 || wanted.Contains(tok))
            {
                var t = md.GetTypeDefinition(m.GetDeclaringType());
                string ns = md.GetString(t.Namespace);
                string tn = md.GetString(t.Name);
                string mn = md.GetString(m.Name);
                Console.WriteLine("0x" + tok.ToString("X8") + " " + (ns.Length == 0 ? tn : ns + "." + tn) + "::" + mn);
            }
        }
    }
}
EOF
  cd "$DIR" || exit 1
  dotnet build -c Release -o bin --nologo -v q > /root/tokdump-build.log 2>&1 || { echo BUILD-FAILED; tail -25 /root/tokdump-build.log; exit 1; }
fi
cd "$DIR" || exit 1
dotnet bin/tokdump.dll "$DLL" "$@" 2>&1 | head -300
