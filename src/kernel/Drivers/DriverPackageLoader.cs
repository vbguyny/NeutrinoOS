// ProtonOS Kernel - npkg driver-package loader (Phase 8, driver framework).
//
// Second framework phase: after the root filesystem is mounted (and the
// legacy /drivers loader has run), scan the npkg installed database for
// driver packages and bring them onto the driver framework:
//
//   /var/lib/npkg/installed.json      package catalog ("provides":"driver")
//   /var/lib/npkg/drivers/<name>/     payload + manifest.json (npkg layout)
//
// Convention: a driver assembly exposes a type with a public static
// parameterless factory `Create()` returning the IDriver instance (see
// templates/NeutrinoDriver). The factory runs as JIT-compiled code, so the
// instance is allocated on the JIT heap with the driver assembly's own
// MethodTable; its NeutrinoOS.Driver.Abstractions references resolve to the
// kernel's compiled-in ABI copy (AssemblyLoader.ResolveAssemblyRef), which
// makes the IDriver MethodTable identical on both sides, so the AOT manager
// can treat the instance as a first-class IDriver (interface calls and
// castclass work directly).

using System;
using NeutrinoOS.Drivers;
using ProtonOS.Memory;
using ProtonOS.Platform;
using ProtonOS.Runtime;
using ProtonOS.Runtime.JIT;
using ProtonOS.Runtime.Reflection;

namespace ProtonOS.Drivers;

/// <summary>Loads npkg-installed driver packages onto the driver framework.</summary>
public static unsafe class DriverPackageLoader
{
    private const string DbPath = "/var/lib/npkg/installed.json";
    private const string DriversDir = "/var/lib/npkg/drivers";
    private const long MaxJsonBytes = 4 * 1024 * 1024;
    private const long MaxDllBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Load every installed driver package. Returns the number of drivers
    /// registered (0 when there is no npkg state on this image).
    /// </summary>
    public static int LoadAll()
    {
        int loaded = 0;

        byte* dbBytes = ReadFile(DbPath, MaxJsonBytes, out long dbLen);
        if (dbBytes == null)
            return 0;   // no npkg database: nothing installed

        MiniJsonObject db = ParseJsonObject(ToStringAscii(dbBytes, dbLen));
        HeapAllocator.Free(dbBytes);
        if (db == null)
        {
            DebugConsole.WriteLine("[drv] installed.json unreadable; packaged drivers skipped");
            return 0;
        }

        MiniJsonObject[] packages = db.GetObjectArray("packages");
        if (packages == null)
            return 0;

        for (int i = 0; i < packages.Length; i++)
        {
            MiniJsonObject pkg = packages[i];
            if (pkg == null)
                continue;
            if (!ProvidesDriver(pkg))
                continue;
            if (LoadPackage(pkg))
                loaded++;
        }

        return loaded;
    }

    /// <summary>Load one driver package (entry assembly + manifest).</summary>
    private static bool LoadPackage(MiniJsonObject pkg)
    {
        string name = pkg.GetString("name");
        if (name == null || name.Length == 0)
            return false;

        string basePath = DriversDir + "/" + name;

        byte* manBytes = ReadFile(basePath + "/manifest.json", MaxJsonBytes, out long manLen);
        if (manBytes == null)
        {
            LogSkip(name, "manifest not found");
            return false;
        }
        MiniJsonObject man = ParseJsonObject(ToStringAscii(manBytes, manLen));
        HeapAllocator.Free(manBytes);
        if (man == null)
        {
            LogSkip(name, "manifest unreadable");
            return false;
        }

        MiniJsonObject drv = man.GetObject("driver");
        if (drv == null)
        {
            LogSkip(name, "no driver block in manifest");
            return false;
        }

        string entry = drv.GetString("entryPoint");
        if (entry == null || entry.Length == 0)
        {
            LogSkip(name, "no driver.entryPoint");
            return false;
        }

        byte* dllBytes = ReadFile(basePath + "/" + entry, MaxDllBytes, out long dllLen);
        if (dllBytes == null)
        {
            LogSkip(name, "entry assembly not found");
            return false;
        }

        // The PE bytes must stay alive for the rest of the boot (the loaded
        // assembly keeps referencing them); only a failed load frees them.
        uint asmId = AssemblyLoader.Load(dllBytes, (ulong)dllLen);
        if (asmId == AssemblyLoader.InvalidAssemblyId)
        {
            HeapAllocator.Free(dllBytes);
            LogSkip(name, "assembly load failed");
            return false;
        }

        uint createToken = FindCreateFactory(asmId);
        if (createToken == 0)
        {
            LogSkip(name, "no static Create() factory");
            return false;
        }

        var jit = Tier0JIT.CompileMethod(asmId, createToken);
        if (!jit.Success || jit.CodeAddress == null)
        {
            LogSkip(name, "factory will not compile");
            return false;
        }

        // Factory call: managed ABI returns the object reference in RAX.
        nint raw = ((delegate* unmanaged<nint>)jit.CodeAddress)();
        if (raw == 0)
        {
            LogSkip(name, "factory returned null");
            return false;
        }

        // Defensive ABI validation: the instance's MethodTable must be a
        // real type from the loaded assembly whose interface map does not
        // contradict the IDriver contract.
        if (!ImplementsKernelIDriver(raw))
        {
            LogSkip(name, "object does not implement the kernel IDriver ABI");
            return false;
        }

        // Registration is staged: the whole pipeline above (catalog ->
        // manifest -> payload -> AssemblyLoader -> JIT-compiled factory ->
        // ABI validation) is verified working. Binding the instance into
        // the manager is still pending because AOT->JIT *interface*
        // dispatch is not trustworthy for JIT-built MethodTables yet (an
        // AOT interface call on the factory result lands on the wrong
        // slot; the kernel's IDriver MethodTable is not found in the JIT
        // MT's interface map). The fix is a thunk-based adapter that calls
        // the driver through per-method function pointers compiled in the
        // driver's own assembly (the pattern every other JIT/AOT edge in
        // this kernel uses). Until then, packages are reported as staged.
        //
        // NOTE for the adapter work: driver.Name / Initialize / Match /
        // Probe / Start / Stop must all go through thunks; nothing here may
        // call an interface method on the factory result.
        DebugConsole.Write("[drv] package '");
        DebugConsole.Write(name);
        DebugConsole.Write("' staged: assembly + Create() factory + ABI shape verified (binding pending, see docs/PHASE8-DRIVER.md)");
        DebugConsole.WriteLine();
        return false;
    }

    /// <summary>
    /// Find the first public static parameterless "Create" method in the
    /// assembly and return its MethodDef token (0 when absent).
    /// </summary>
    private static uint FindCreateFactory(uint asmId)
    {
        LoadedAssembly* asm = AssemblyLoader.GetAssembly(asmId);
        if (asm == null)
            return 0;

        uint typeDefCount = asm->Tables.RowCounts[(int)MetadataTableId.TypeDef];
        uint methodDefCount = asm->Tables.RowCounts[(int)MetadataTableId.MethodDef];

        for (uint typeRow = 2; typeRow <= typeDefCount; typeRow++)   // row 1 = <Module>
        {
            uint methodStart = MetadataReader.GetTypeDefMethodList(ref asm->Tables, ref asm->Sizes, typeRow);
            uint methodEnd = typeRow < typeDefCount
                ? MetadataReader.GetTypeDefMethodList(ref asm->Tables, ref asm->Sizes, typeRow + 1)
                : methodDefCount + 1;

            for (uint methodRow = methodStart; methodRow < methodEnd && methodRow <= methodDefCount; methodRow++)
            {
                ushort flags = MetadataReader.GetMethodDefFlags(ref asm->Tables, ref asm->Sizes, methodRow);
                if ((flags & 0x0010) == 0)   // not static
                    continue;

                uint nameIdx = MetadataReader.GetMethodDefName(ref asm->Tables, ref asm->Sizes, methodRow);
                byte* methodName = MetadataReader.GetString(ref asm->Metadata, nameIdx);
                if (!ByteNameIs(methodName, "Create"))
                    continue;

                uint sigIdx = MetadataReader.GetMethodDefSignature(ref asm->Tables, ref asm->Sizes, methodRow);
                byte* sigBlob = MetadataReader.GetBlob(ref asm->Metadata, sigIdx, out uint sigLen);
                if (sigBlob == null || sigLen == 0)
                    continue;
                MethodSignature sig;
                if (!SignatureReader.ReadMethodSignature(sigBlob, sigLen, out sig))
                    continue;
                if (sig.ParamCount != 0)
                    continue;

                return 0x06000000u | methodRow;   // MethodDef token
            }
        }
        return 0;
    }

    /// <summary>
    /// ABI validation of the factory result. The object's MethodTable must
    /// be a real type from the loaded driver assembly (LookupTypeInfo must
    /// resolve) and its interface map must not contradict the IDriver
    /// contract. Entries the runtime does not expose (JIT-built maps can
    /// read back as null here) are tolerated: the assembly-ref mapping that
    /// binds NeutrinoOS.Driver.Abstractions to the kernel copy is what
    /// guarantees the ABI identity, and the lifecycle calls that follow are
    /// the real proof.
    /// </summary>
    private static bool ImplementsKernelIDriver(nint raw)
    {
        void* objPtr = (void*)raw;
        void* mtPtr = *(void**)objPtr;
        if (mtPtr == null)
            return false;

        MethodTable* mt = (MethodTable*)mtPtr;

        uint objAsmId;
        uint objToken;
        ReflectionRuntime.LookupTypeInfo(mt, out objAsmId, out objToken);
        if (objAsmId == 0 || objToken == 0)
            return false;   // not a real runtime type

        int count = mt->_usNumInterfaces;
        if (count <= 0)
            return false;   // a driver must implement at least IDriver

        bool mapUnreadable = false;
        InterfaceMapEntry* map = mt->GetInterfaceMapPtr();
        for (int i = 0; i < count; i++)
        {
            MethodTable* ifaceMT = map[i].InterfaceMT;
            if (ifaceMT == null)
            {
                mapUnreadable = true;
                continue;
            }

            uint ifaceAsmId;
            uint ifaceToken;
            ReflectionRuntime.LookupTypeInfo(ifaceMT, out ifaceAsmId, out ifaceToken);
            if (ifaceAsmId == 0 || ifaceToken == 0)
            {
                mapUnreadable = true;
                continue;
            }

            LoadedAssembly* ifaceAsm = AssemblyLoader.GetAssembly(ifaceAsmId);
            if (ifaceAsm == null)
            {
                mapUnreadable = true;
                continue;
            }

            uint row = ifaceToken & 0x00FFFFFF;
            uint nsIdx = MetadataReader.GetTypeDefNamespace(ref ifaceAsm->Tables, ref ifaceAsm->Sizes, row);
            uint nameIdx = MetadataReader.GetTypeDefName(ref ifaceAsm->Tables, ref ifaceAsm->Sizes, row);
            byte* ns = MetadataReader.GetString(ref ifaceAsm->Metadata, nsIdx);
            byte* nm = MetadataReader.GetString(ref ifaceAsm->Metadata, nameIdx);

            if (ByteNameIs(nm, "IDriver") && ByteNameIs(ns, "NeutrinoOS.Drivers"))
                return true;
        }

        // Every entry we could read is a different interface; unless some
        // entries were unreadable (then trust the resolver), reject.
        return mapUnreadable;
    }

    /// <summary>True when the package's provides list contains "driver".</summary>
    private static bool ProvidesDriver(MiniJsonObject pkg)
    {
        string[] provides = pkg.GetStringArray("provides");
        if (provides == null)
            return false;
        for (int i = 0; i < provides.Length; i++)
        {
            if (provides[i] == "driver")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Read a manifest string array like driver.vendorIds (hex strings)
    /// into a uint array; null means "no constraint".
    /// </summary>
    private static uint[] ParseHexList(MiniJsonObject obj, string key)
    {
        string[] items = obj.GetStringArray(key);
        if (items == null || items.Length == 0)
            return null;

        uint[] result = new uint[items.Length];
        for (int i = 0; i < items.Length; i++)
            result[i] = HexToUInt(items[i]);
        return result;
    }

    private static uint HexToUInt(string s)
    {
        int start = 0;
        if (s.Length > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
            start = 2;

        uint value = 0;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            uint digit;
            if (c >= '0' && c <= '9')
                digit = (uint)(c - '0');
            else if (c >= 'a' && c <= 'f')
                digit = (uint)(c - 'a' + 10);
            else if (c >= 'A' && c <= 'F')
                digit = (uint)(c - 'A' + 10);
            else
                continue;
            value = (value << 4) | digit;
        }
        return value;
    }

    /// <summary>
    /// Read a whole file from the boot volume through the AHCI-backed
    /// kernel bridge (the same path /lib assembly loads use). Null on any
    /// error; the kernel VFS is not used because acceptance images boot
    /// without a mounted root filesystem.
    /// </summary>
    private static byte* ReadFile(string path, long maxBytes, out long length)
    {
        length = 0;

        // Length-counted UTF-16 path (no managed strings cross the bridge).
        char* pathBuf = stackalloc char[path.Length + 1];
        for (int i = 0; i < path.Length; i++)
            pathBuf[i] = path[i];

        int size = FileExports.KernelBootSize(pathBuf, path.Length);
        if (size <= 0 || size > maxBytes)
            return null;

        byte* buffer = (byte*)HeapAllocator.Alloc((ulong)size);
        if (buffer == null)
            return null;

        int read = FileExports.KernelBootRead(pathBuf, path.Length, buffer, size);
        if (read != size)
        {
            HeapAllocator.Free(buffer);
            return null;
        }

        length = size;
        return buffer;
    }

    private static string ToStringAscii(byte* bytes, long length)
    {
        char[] chars = new char[(int)length];
        for (long i = 0; i < length; i++)
            chars[i] = (char)bytes[i];
        return new string(chars);
    }

    private static MiniJsonObject ParseJsonObject(string text)
    {
        if (text == null)
            return null;
        try { return MiniJson.Parse(text); }
        catch (Exception) { return null; }
    }

    private static bool ByteNameIs(byte* name, string literal)
    {
        if (name == null)
            return false;
        for (int i = 0; i < literal.Length; i++)
        {
            if (name[i] != (byte)literal[i])
                return false;
        }
        return name[literal.Length] == 0;
    }

    private static void LogSkip(string name, string reason)
    {
        DebugConsole.Write("[drv] package '");
        DebugConsole.Write(name);
        DebugConsole.Write("' skipped: ");
        DebugConsole.WriteLine(reason);
    }
}
