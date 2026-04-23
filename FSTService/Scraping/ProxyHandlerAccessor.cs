namespace FSTService.Scraping;

/// <summary>
/// Holds a reference to the active <see cref="RoundRobinProxyHandler"/> so that diagnostic
/// endpoints can inspect proxy slot state without refactoring the typed-HttpClient pipeline.
/// Null when proxy rotation is not configured.
/// </summary>
public sealed class ProxyHandlerAccessor
{
    private RoundRobinProxyHandler? _handler;

    public RoundRobinProxyHandler? Handler => _handler;

    public void Set(RoundRobinProxyHandler handler) => _handler = handler;
}
