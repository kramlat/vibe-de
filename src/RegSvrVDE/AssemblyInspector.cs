using System.Reflection;
using System.Runtime.InteropServices;
using Vde.Components;

namespace RegSvrVDE;

public sealed record Inspection(
    IReadOnlyList<ComponentInfo> Components,
    string? InstallerType,
    IReadOnlyList<string> Warnings);

// Reads which components an assembly declares WITHOUT running any of its code. The assembly is opened with
// MetadataLoadContext, which only parses its metadata: no static constructor, module initializer or
// attribute constructor runs, so registering an untrusted file cannot execute it.
internal static class AssemblyInspector
{
    private const string ComponentAttribute = "Vde.Components.VdeComponentAttribute";
    private const string InstallerAttribute = "Vde.Components.VdeInstallerAttribute";

    public static Inspection Inspect(string path)
    {
        path = Path.GetFullPath(path);

        // Everything the target might refer to: the .NET runtime, its own folder, and our Vde.Components.
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[] { RuntimeEnvironment.GetRuntimeDirectory(), Path.GetDirectoryName(path)!, AppContext.BaseDirectory })
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
                byName.TryAdd(Path.GetFileName(file), file);
        byName[Path.GetFileName(path)] = path;

        using var context = new MetadataLoadContext(new PathAssemblyResolver(byName.Values), "System.Runtime");
        var assembly = context.LoadFromAssemblyPath(path);   // BadImageFormatException when it is not a .NET assembly

        var warnings = new List<string>();
        var components = new List<ComponentInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        IEnumerable<Type> types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.Where(t => t is not null).Select(t => t!);
            warnings.Add("some types could not be examined: " + e.LoaderExceptions.FirstOrDefault()?.Message);
        }

        foreach (var type in types)
        {
            var data = CustomAttributeData.GetCustomAttributes(type).FirstOrDefault(a => a.AttributeType.FullName == ComponentAttribute);
            if (data is null)
                continue;

            var name = type.FullName ?? type.Name;
            if (ComponentInfo.NormalizeGuid(data.ConstructorArguments.FirstOrDefault().Value as string) is not { } clsid)
            {
                warnings.Add($"{name}: '{data.ConstructorArguments.FirstOrDefault().Value}' is not a GUID; skipped");
                continue;
            }
            if (!type.IsPublic || type.IsAbstract || type.IsGenericTypeDefinition)
            {
                warnings.Add($"{name}: a component must be a public, non-abstract, non-generic class; skipped");
                continue;
            }
            if (!seen.Add(clsid))
            {
                warnings.Add($"{name}: {clsid} is already used by another class in this assembly; skipped");
                continue;
            }

            string? progId = null, description = null;
            var threading = ThreadingModel.Apartment;
            var categories = new List<string>();
            foreach (var named in data.NamedArguments)
            {
                switch (named.MemberName)
                {
                    case "ProgId": progId = named.TypedValue.Value as string; break;
                    case "Description": description = named.TypedValue.Value as string; break;
                    case "ThreadingModel": threading = (ThreadingModel)Convert.ToInt32(named.TypedValue.Value); break;
                    case "Categories":
                        foreach (var item in (IEnumerable<CustomAttributeTypedArgument>?)named.TypedValue.Value ?? [])
                        {
                            if (ComponentInfo.NormalizeGuid(item.Value as string) is { } category)
                                categories.Add(category);
                            else
                                warnings.Add($"{name}: category '{item.Value}' is not a GUID; ignored");
                        }
                        break;
                }
            }

            components.Add(new ComponentInfo(clsid, name, path, assembly.FullName ?? Path.GetFileNameWithoutExtension(path), threading, progId, description, categories));
        }

        string? installer = null;
        var installerData = CustomAttributeData.GetCustomAttributes(assembly).FirstOrDefault(a => a.AttributeType.FullName == InstallerAttribute);
        if (installerData?.ConstructorArguments.FirstOrDefault().Value is Type installerType)
            installer = installerType.FullName;

        return new Inspection(components, installer, warnings);
    }
}
