// NeutrinoOS korlib - System.IServiceProvider
//
// The BCL shape DI code targets: ASP.NET-style service resolution
// (GetService/GetRequiredService extension methods in the DDK's
// Microsoft.Extensions.DependencyInjection shim) is written against
// System.IServiceProvider, so the type must exist in the runtime.

namespace System;

/// <summary>Resolves a service instance by type (null when absent).</summary>
public interface IServiceProvider
{
    /// <summary>Resolve a service by type, or null when absent.</summary>
    object? GetService(Type serviceType);
}
