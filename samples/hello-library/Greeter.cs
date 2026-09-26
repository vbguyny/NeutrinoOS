// hello-library - the library half of the SDK sample pair.

using System;

namespace HelloLibrary;

public static class Greeter
{
    /// <summary>Returns a greeting for the given name (or "world").</summary>
    public static string Message(string name)
    {
        return "Hello, " + (string.IsNullOrEmpty(name) ? "world" : name) + "!";
    }

    /// <summary>Environment snapshot shown by the console sample.</summary>
    public static string Describe()
    {
        return "runtime=" + Environment.Version +
               " cpus=" + Environment.ProcessorCount;
    }
}
