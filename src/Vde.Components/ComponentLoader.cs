using System.Reflection;

namespace Vde.Components;

public sealed class ComponentLoadException(string message, Exception? inner = null) : Exception(message, inner);

// Turns a registration into a live object. This is the one place a component's code is loaded, so it is also
// where trust is enforced: nothing untrusted is ever loaded, however it got registered.
//
// Threading: a component registered Apartment must only be used on the thread that created it (the UI thread);
// Free and Both ones may be used from anywhere. The loader records the creating thread so a host can check.
public sealed class ComponentLoader
{
    private readonly Dictionary<string, object> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _threads = new(StringComparer.Ordinal);

    // The single shared instance of the component (created on first use), as the interface T.
    public T Get<T>(ComponentRegistration registration) where T : class
    {
        var info = registration.Component;
        if (!registration.Trust.IsTrusted)
            throw new ComponentLoadException($"{info.ClassName} is not trusted: {registration.Trust.Reason}");

        lock (_instances)
        {
            if (!_instances.TryGetValue(info.Clsid, out var instance))
            {
                try
                {
                    var assembly = Assembly.LoadFrom(info.AssemblyPath);
                    var type = assembly.GetType(info.ClassName, throwOnError: true)!;
                    instance = Activator.CreateInstance(type) ?? throw new ComponentLoadException($"{info.ClassName} could not be created.");
                }
                catch (Exception e) when (e is not ComponentLoadException)
                {
                    throw new ComponentLoadException($"{info.ClassName} failed to load: {(e is TargetInvocationException { InnerException: { } inner } ? inner : e).Message}", e);
                }
                _instances[info.Clsid] = instance;
                _threads[info.Clsid] = Environment.CurrentManagedThreadId;
            }

            if (info.ThreadingModel == ThreadingModel.Apartment && _threads[info.Clsid] != Environment.CurrentManagedThreadId)
                throw new ComponentLoadException($"{info.ClassName} is an apartment-threaded component and may only be used on the thread that created it.");
            return instance as T ?? throw new ComponentLoadException($"{info.ClassName} does not implement {typeof(T).Name}.");
        }
    }
}
