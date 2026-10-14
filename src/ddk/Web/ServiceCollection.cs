// NeutrinoOS Web - Kestrel port milestone M4: the minimal DI shim.
//
// The Microsoft.Extensions.DependencyInjection subset minimal-API apps
// use: instance (singleton) registration by service type and resolution
// through GetService/GetRequiredService. It mirrors the ASP.NET Core
// flow for the shapes the port supports today:
//
//   var builder = WebApplication.CreateBuilder(args);
//   builder.Services.AddSingleton<ILoggerFactory>(new ConsoleLoggerFactory("webapp"));
//   var app = builder.Build();
//   ...
//   var lf = app.Services.GetRequiredService<ILoggerFactory>();
//
// Constraints (documented in docs/KESTREL-PORT.md):
//   - singletons only: instances are registered, not factory/implementation
//     types (AddSingleton<TService>(TService instance) - no
//     AddSingleton<TService, TImpl> construction);
//   - service types must be reference types or interfaces
//     (where TService : class, matching the shapes ASP.NET DI is used for).
//
// The generic surface resolves through a non-generic core (Type + object
// storage) - the same typeof-token comparison + cast pattern the feature
// collection uses, which is pinned on-device by the JITTest suites.

using System;

namespace NeutrinoOS.DDK.Web;

/// <summary>Minimal service collection/provider (see file header).</summary>
public sealed class ServiceCollection
{
    private const int MaxServices = 24;

    private readonly Type[] _types = new Type[MaxServices];
    private readonly object[] _instances = new object[MaxServices];
    private int _count;

    /// <summary>
    /// Register (or replace) <paramref name="instance"/> as the singleton
    /// for <typeparamref name="TService"/>.
    /// </summary>
    public ServiceCollection AddSingleton<TService>(TService instance) where TService : class
    {
        Add(typeof(TService), (object)instance);
        return this;
    }

    /// <summary>Register (or replace) an instance for a service type.</summary>
    public void Add(Type serviceType, object instance)
    {
        if (serviceType == null || instance == null)
            return;

        for (int i = 0; i < _count; i++)
        {
            if (_types[i] == serviceType)
            {
                _instances[i] = instance;   // replace an existing registration
                return;
            }
        }

        if (_count >= MaxServices)
            return;
        _types[_count] = serviceType;
        _instances[_count] = instance;
        _count++;
    }

    /// <summary>Find the registered instance for a service type, or null.</summary>
    public object? Find(Type serviceType)
    {
        if (serviceType == null)
            return null;
        for (int i = 0; i < _count; i++)
        {
            if (_types[i] == serviceType)
                return _instances[i];
        }
        return null;
    }

    /// <summary>Resolve <typeparamref name="TService"/>, or null when absent.</summary>
    public TService? GetService<TService>() where TService : class
    {
        object? instance = Find(typeof(TService));
        if (instance == null)
            return null;
        return (TService)instance;
    }

    /// <summary>Resolve <typeparamref name="TService"/>; throws when absent.</summary>
    public TService GetRequiredService<TService>() where TService : class
    {
        TService? instance = GetService<TService>();
        if (instance == null)
            throw new System.InvalidOperationException("no service registered for this type");
        return instance;
    }

    /// <summary>Number of registrations (diagnostics/tests).</summary>
    public int Count => _count;
}
