// NeutrinoOS - ASP.NET Core namespace compatibility layer (M7):
// Microsoft.Extensions.DependencyInjection.
//
// The DI surfaces minimal-API apps use:
//
//   builder.Services.AddSingleton<AppInfo>(new AppInfo(...));
//   var svc = app.Services.GetRequiredService<AppInfo>();
//
// Storage/registry is the DDK's ServiceCollection (which implements
// both the marker interface below and System.IServiceProvider - korlib
// added the BCL type so this code compiles for the device AND Windows).
// Documented limits in docs/KESTREL-PORT.md M7: singleton instances
// only (no factory/implementation-type registration).

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Service registrations (marker; implemented by the DDK collection).</summary>
public interface IServiceCollection
{
}

/// <summary>AddSingleton/AddSingleton&lt;T&gt; shapes for the DDK collection.</summary>
public static class ServiceCollectionServiceExtensions
{
    /// <summary>Register (or replace) an instance as the singleton for TService.</summary>
    public static IServiceCollection AddSingleton<TService>(this IServiceCollection services, TService instance)
        where TService : class
    {
        var collection = services as NeutrinoOS.DDK.Web.ServiceCollection;
        if (collection == null)
            throw new System.InvalidOperationException("service collection is not a NeutrinoOS collection");
        collection.AddSingleton<TService>(instance);
        return services;
    }
}

/// <summary>GetService/GetRequiredService shapes on System.IServiceProvider.</summary>
public static class ServiceProviderServiceExtensions
{
    /// <summary>Resolve TService, or null when absent.</summary>
    public static TService GetService<TService>(this System.IServiceProvider provider)
        where TService : class
    {
        return (TService)provider.GetService(typeof(TService));
    }

    /// <summary>Resolve TService; throws when absent.</summary>
    public static TService GetRequiredService<TService>(this System.IServiceProvider provider)
        where TService : class
    {
        object instance = provider.GetService(typeof(TService));
        if (instance == null)
            throw new System.InvalidOperationException("no service registered for this type");
        return (TService)instance;
    }
}
