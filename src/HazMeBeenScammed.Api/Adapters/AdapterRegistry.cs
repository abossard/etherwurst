using HazMeBeenScammed.Core.Ports;

namespace HazMeBeenScammed.Api.Adapters;

/// <summary>
/// Registry that holds all available blockchain adapter backends.
/// Allows runtime selection per request via a simple string key.
/// </summary>
public interface IAdapterRegistry
{
    IBlockchainAnalyticsPort GetAdapter(string? backendName);
    IReadOnlyList<string> AvailableBackends { get; }
    string DefaultBackend { get; }
}

public sealed class AdapterRegistry : IAdapterRegistry
{
    private readonly Dictionary<string, IBlockchainAnalyticsPort> _adapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _defaultBackend;

    public AdapterRegistry(string defaultBackend)
    {
        _defaultBackend = defaultBackend;
    }

    public void Register(string name, IBlockchainAnalyticsPort adapter)
    {
        _adapters[name] = adapter;
    }

    public IBlockchainAnalyticsPort GetAdapter(string? backendName)
    {
        var key = backendName ?? _defaultBackend;
        if (_adapters.TryGetValue(key, out var adapter))
            return adapter;

        // Fall back to default
        if (_adapters.TryGetValue(_defaultBackend, out var defaultAdapter))
            return defaultAdapter;

        throw new InvalidOperationException(
            $"No adapter registered for '{key}'. Available: {string.Join(", ", _adapters.Keys)}");
    }

    public IReadOnlyList<string> AvailableBackends => _adapters.Keys.ToList();
    public string DefaultBackend => _defaultBackend;
}
