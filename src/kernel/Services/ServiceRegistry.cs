// NeutrinoOS kernel - cooperative service registry (Phase 6)
//
// Background services (sshd, the web host, ...) live in the JIT world
// (NeutrinoOS.DDK). The kernel starts them on demand by compiling their
// entry points from the DDK assembly - the same technique the network
// bridge uses for the virtio-net frame pump - and then calls each
// service's Tick() from the shell idle hook, cooperatively on the boot
// thread. There are no extra threads and no JIT reentrancy concerns.
//
// A service type must expose, in the DDK assembly:
//   public static int  Start()   - initialize (0 = ok)
//   public static void Tick()    - bounded slice of work
//   [optional] Stop()
//
// (Function pointers are kept in discrete fields: the AOT compiler
// does not support arrays of pointer types.)

using System;
using NeutrinoOS.Platform;
using NeutrinoOS.Runtime;

namespace NeutrinoOS.Services;

/// <summary>Cooperative background service registry (see file header).</summary>
public static unsafe class ServiceRegistry
{
    private const int MaxServices = 4;

    private static readonly string[] _names = new string[MaxServices];
    private static void* _tick0;
    private static void* _tick1;
    private static void* _tick2;
    private static void* _tick3;
    private static void* _stop0;
    private static void* _stop1;
    private static void* _stop2;
    private static void* _stop3;
    private static int _count;

    /// <summary>Number of running services.</summary>
    public static int Count => _count;

    /// <summary>Name of the running service at index, or null.</summary>
    public static string? NameAt(int index)
        => index >= 0 && index < _count ? _names[index] : null;

    private static void* GetTick(int i)
    {
        if (i == 0) return _tick0;
        if (i == 1) return _tick1;
        if (i == 2) return _tick2;
        return _tick3;
    }

    private static void* GetStop(int i)
    {
        if (i == 0) return _stop0;
        if (i == 1) return _stop1;
        if (i == 2) return _stop2;
        return _stop3;
    }

    private static void SetSlot(int i, void* tick, void* stop)
    {
        if (i == 0) { _tick0 = tick; _stop0 = stop; }
        else if (i == 1) { _tick1 = tick; _stop1 = stop; }
        else if (i == 2) { _tick2 = tick; _stop2 = stop; }
        else { _tick3 = tick; _stop3 = stop; }
    }

    private static void MoveSlot(int dst, int src)
    {
        SetSlot(dst, GetTick(src), GetStop(src));
        _names[dst] = _names[src];
    }

    /// <summary>
    /// Start a named DDK service. Returns 0 on success (or when already
    /// running), negative on resolution/compile errors, or the service's
    /// own non-zero Start() result.
    /// </summary>
    public static int Start(string name)
    {
        if (name == null || name.Length == 0)
            return -1;

        for (int i = 0; i < _count; i++)
        {
            if (_names[i] == name)
                return 0;   // already running
        }

        if (_count >= MaxServices)
            return -2;

        string ns;
        string type;
        if (name == "sshd")
        {
            ns = "NeutrinoOS.DDK.Services";
            type = "SshService";
        }
        else if (name == "webhost")
        {
            ns = "NeutrinoOS.DDK.Services";
            type = "WebService";
        }
        else if (name == "sampleapi")
        {
            ns = "NeutrinoOS.DDK.Services";
            type = "SampleApi";
        }
        else if (name == "webapi")
        {
            // WebApplication-style sample app (docs/KESTREL-PORT.md M1).
            ns = "NeutrinoOS.DDK.Samples";
            type = "TasksWebApp";
        }
        else
        {
            return -3;
        }

        uint ddkId = Kernel.DdkAssemblyId;
        if (ddkId == AssemblyLoader.InvalidAssemblyId)
            return -4;

        uint typeToken = AssemblyLoader.FindTypeDefByFullName(ddkId, ns, type);
        if (typeToken == 0)
            return -5;

        uint startToken = AssemblyLoader.FindMethodDefByName(ddkId, typeToken, "Start");
        uint tickToken = AssemblyLoader.FindMethodDefByName(ddkId, typeToken, "Tick");
        if (startToken == 0 || tickToken == 0)
            return -6;

        var startResult = Runtime.JIT.Tier0JIT.CompileMethod(ddkId, startToken);
        if (!startResult.Success || startResult.CodeAddress == null)
            return -7;
        var tickResult = Runtime.JIT.Tier0JIT.CompileMethod(ddkId, tickToken);
        if (!tickResult.Success || tickResult.CodeAddress == null)
            return -8;

        var startFn = (delegate* unmanaged<int>)startResult.CodeAddress;
        int serviceResult = startFn();
        if (serviceResult != 0)
            return serviceResult;

        uint stopToken = AssemblyLoader.FindMethodDefByName(ddkId, typeToken, "Stop");
        void* stopPtr = null;
        if (stopToken != 0)
        {
            var stopResult = Runtime.JIT.Tier0JIT.CompileMethod(ddkId, stopToken);
            if (stopResult.Success)
                stopPtr = stopResult.CodeAddress;
        }

        SetSlot(_count, tickResult.CodeAddress, stopPtr);
        _names[_count] = name;
        _count++;
        DebugConsole.Write("[Services] started ");
        DebugConsole.WriteLine(name);
        return 0;
    }

    /// <summary>
    /// M4: launch a web app DLL (e.g. /apps/webapp/webapp.dll) as a
    /// cooperative hosted app. <paramref name="tokens"/>[0] is the DLL
    /// path, the rest are passed to the app's Main.
    ///
    /// The DDK's WebAppRuntime helpers are compiled by name (the same
    /// technique as Start): SetHosted() flips the runtime into hosted
    /// mode, the app's Main runs (it builds a WebApplication and calls
    /// app.Run(), which starts the listener, registers with
    /// WebAppRuntime and returns), then the registry-count check tells us
    /// whether the app took the hosted path. TickAll/StopAll become the
    /// kernel-side "webapp" service tick once; later apps join the same
    /// set. Returns 0 on success, negative on resolution/compile errors,
    /// or the app's own non-zero exit code.
    /// </summary>
    public static int StartApp(string[] tokens)
    {
        if (tokens == null || tokens.Length == 0)
            return -1;
        string path = tokens[0];
        if (path == null || path.Length == 0)
            return -1;

        uint ddkId = Kernel.DdkAssemblyId;
        if (ddkId == AssemblyLoader.InvalidAssemblyId)
            return -4;

        uint typeToken = AssemblyLoader.FindTypeDefByFullName(ddkId, "NeutrinoOS.DDK.Web", "WebAppRuntime");
        if (typeToken == 0)
            return -5;

        void* setHosted = CompileDdkStatic(ddkId, typeToken, "SetHosted");
        void* clearHosted = CompileDdkStatic(ddkId, typeToken, "ClearHosted");
        void* getCount = CompileDdkStatic(ddkId, typeToken, "GetHostedCount");
        if (setHosted == null || clearHosted == null || getCount == null)
            return -6;

        ((delegate* unmanaged<void>)setHosted)();
        int before = ((delegate* unmanaged<int>)getCount)();

        var appArgs = new string[tokens.Length - 1];
        for (int i = 1; i < tokens.Length; i++)
            appArgs[i - 1] = tokens[i];

        int rc = AssemblyRunner.RunOnCurrentStack(path, appArgs);

        int hostedCount = ((delegate* unmanaged<int>)getCount)();
        ((delegate* unmanaged<void>)clearHosted)();

        if (hostedCount <= before)
            return rc != 0 ? rc : -7;   // app never called Run()/registered

        // Register the kernel-side tick once; later apps join the same set.
        for (int i = 0; i < _count; i++)
        {
            if (_names[i] == "webapp")
                return 0;
        }

        if (_count >= MaxServices)
            return -2;

        void* appTick = CompileDdkStatic(ddkId, typeToken, "TickAll");
        void* appStop = CompileDdkStatic(ddkId, typeToken, "StopAll");
        if (appTick == null)
            return -8;

        SetSlot(_count, appTick, appStop);
        _names[_count] = "webapp";
        _count++;
        DebugConsole.Write("[Services] hosted app: ");
        DebugConsole.WriteLine(path);
        return 0;
    }

    /// <summary>Compile one parameterless DDK static by name; null on failure.</summary>
    private static void* CompileDdkStatic(uint ddkId, uint typeToken, string methodName)
    {
        uint token = AssemblyLoader.FindMethodDefByName(ddkId, typeToken, methodName);
        if (token == 0)
            return null;
        var result = Runtime.JIT.Tier0JIT.CompileMethod(ddkId, token);
        return result.Success ? result.CodeAddress : null;
    }

    /// <summary>Stop a named service (runs its Stop() when available).</summary>
    public static int Stop(string name)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_names[i] == name)
            {
                void* stopPtr = GetStop(i);
                if (stopPtr != null)
                {
                    var stopFn = (delegate* unmanaged<void>)stopPtr;
                    stopFn();
                }
                for (int j = i; j < _count - 1; j++)
                    MoveSlot(j, j + 1);
                _count--;
                _names[_count] = null;
                SetSlot(_count, null, null);
                return 0;
            }
        }
        return -1;
    }

    /// <summary>Run one bounded slice of every running service.</summary>
    public static void Tick()
    {
        for (int i = 0; i < _count; i++)
        {
            var tickFn = (delegate* unmanaged<void>)GetTick(i);
            tickFn();
        }
    }
}
