// NeutrinoOS korlib - System.Environment (Phase 5 environment variables)
//
// Implements the process environment: a fixed-size table of NAME=VALUE
// strings shared by the kernel shell and every JIT-compiled application
// (both resolve to this same AOT implementation).
//
// Deviations from the official BCL (documented per the Phase 5 spec):
//   - GetEnvironmentVariables() returns IDictionary in the BCL;
//     NeutrinoOS cannot use generic dictionaries in kernel AOT code, so
//     the NeutrinoOS extension GetEnvironmentVariableNames() returns the
//     names as a string[] (the env utility iterates it).
//   - Setting a variable to null or "" removes it (the shell's `unset`
//     relies on this; the official BCL sets an empty value).
//   - The table holds up to 64 variables with values up to 1023 chars.

using System.IO;

namespace System;

public static partial class Environment
{
    private const int MaxVariables = 64;
    private const int MaxValueLength = 1023;

    private static readonly string?[] _varNames = new string?[MaxVariables];
    private static readonly string?[] _varValues = new string?[MaxVariables];
    private static int _varCount;

    private static int FindVariable(string name)
    {
        for (int i = 0; i < _varCount; i++)
        {
            string? n = _varNames[i];
            if (n != null && n == name)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Retrieves the value of an environment variable, or null when the
    /// variable is not set. Name matching is exact (case-sensitive).
    /// </summary>
    public static string? GetEnvironmentVariable(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentNullException("name");
        int i = FindVariable(name);
        return i < 0 ? null : _varValues[i];
    }

    /// <summary>
    /// Sets (or removes, when <paramref name="value"/> is null or empty)
    /// an environment variable. The change is visible to the shell and to
    /// subsequently started applications.
    /// </summary>
    public static void SetEnvironmentVariable(string name, string? value)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentNullException("name");
        if (name.IndexOf('=') >= 0)
            throw new ArgumentException("Environment variable name cannot contain '='");

        int i = FindVariable(name);
        bool remove = string.IsNullOrEmpty(value);

        if (i >= 0)
        {
            if (remove)
            {
                // Compact the table (order of the remaining entries is kept).
                for (int k = i; k < _varCount - 1; k++)
                {
                    _varNames[k] = _varNames[k + 1];
                    _varValues[k] = _varValues[k + 1];
                }
                _varCount--;
                _varNames[_varCount] = null;
                _varValues[_varCount] = null;
            }
            else
            {
                _varValues[i] = Truncate(value!);
            }
            return;
        }

        if (remove)
            return;
        if (_varCount >= MaxVariables)
            throw new IOException("Environment variable table is full (64 entries)");

        _varNames[_varCount] = name;
        _varValues[_varCount] = Truncate(value!);
        _varCount++;
    }

    /// <summary>
    /// NeutrinoOS Phase 5 extension: the names of all set environment
    /// variables (in insertion order). Used by the env utility in place
    /// of the BCL's GetEnvironmentVariables() dictionary.
    /// </summary>
    public static string[] GetEnvironmentVariableNames()
    {
        string[] result = new string[_varCount];
        for (int i = 0; i < _varCount; i++)
            result[i] = _varNames[i]!;
        return result;
    }

    private static string Truncate(string value)
    {
        if (value.Length > MaxValueLength)
            return value.Substring(0, MaxValueLength);
        return value;
    }
}
