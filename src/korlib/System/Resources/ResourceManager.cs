// NeutrinoOS korlib - System.Resources.ResourceManager minimal implementation.
//
// Framework libraries (Microsoft.Extensions.*, BCL) generate an internal `SR`
// class whose resource strings come from a ResourceManager. On NeutrinoOS there
// is no satellite resource infrastructure, so GetString always returns null and
// the generated SR helpers fall back to their hardcoded default strings.
namespace System.Resources
{
    public class ResourceManager
    {
        private readonly Type? _resourceSource;

        public ResourceManager(Type resourceSource)
        {
            _resourceSource = resourceSource;
        }

        /// <summary>The type backing this manager, when constructed from a type.</summary>
        public virtual Type? ResourceSource => _resourceSource;

        /// <summary>No resources on NeutrinoOS: callers use their fallback strings.</summary>
        public virtual string? GetString(string name)
        {
            _ = name;
            return null;
        }
    }
}
