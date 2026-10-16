using System.Runtime.CompilerServices;
using Vde.Components;
using Vibe.Registry;

[assembly: VdeInstaller(typeof(Sample.Component.SampleInstaller))]

namespace Sample.Component;

[VdeComponent("{5a0e2d4c-0000-4000-8000-000000000001}", ProgId = "Sample.Portal", Description = "Sample portal backend",
    ThreadingModel = ThreadingModel.Apartment, Categories = ["{aaaaaaaa-0000-4000-8000-000000000001}", "{aaaaaaaa-0000-4000-8000-000000000002}"])]
public class PortalBackend
{
}

[VdeComponent("{5A0E2D4C-0000-4000-8000-000000000002}", ThreadingModel = ThreadingModel.Free)]
public class Worker
{
}

// Declared but not registrable: the tool must say so and skip them.
[VdeComponent("{5A0E2D4C-0000-4000-8000-000000000003}")]
internal class NotPublic
{
}

[VdeComponent("not-a-guid")]
public class BadGuid
{
}

[VdeComponent("{5A0E2D4C-0000-4000-8000-000000000004}")]
public abstract class Abstract
{
}

public static class Marker
{
    // Runs the moment this assembly is loaded for execution, and not when only its metadata is read.
    [ModuleInitializer]
    public static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("VDE_SAMPLE_MARKER") is { Length: > 0 } path)
            File.AppendAllText(path, "module initializer ran\n");
    }
}

public sealed class SampleInstaller : IVdeInstaller
{
    public void Install(VdeInstallContext context)
    {
        if (context.CommandLine == "fail")
            throw new InvalidOperationException("the installer was told to fail");

        context.Registry.SetValue("Sample.Settings", "Installing", RegistryValueKind.String, context.Installing ? "yes" : "no");
        context.Registry.SetValue("Sample.Settings", "CommandLine", RegistryValueKind.String, context.CommandLine ?? "");
        context.Registry.SetValue("Sample.Settings", "Assembly", RegistryValueKind.String, Path.GetFileName(context.AssemblyPath));
    }
}

[VdeComponent("{5A0E2D4C-0000-4000-8000-000000000010}", ProgId = "Sample.PropertyPages", Description = "Sample property pages",
    ThreadingModel = ThreadingModel.Free)]
public class SamplePropertyPages : IPropertyPageProvider
{
    public IReadOnlyList<PropertyPageInfo> GetPages(PropertyTarget target)
        => [new PropertyPageInfo("Sample", () => "content for " + string.Join(",", target.Paths.Select(Path.GetFileName)), () => true, Order: 50)];
}

[VdeComponent("{5A0E2D4C-0000-4000-8000-000000000011}", ThreadingModel = ThreadingModel.Apartment)]
public class ApartmentThing : IPropertyPageProvider
{
    public IReadOnlyList<PropertyPageInfo> GetPages(PropertyTarget target) => [];
}

[VdeComponent("{5A0E2D4C-0000-4000-8000-000000000012}", ThreadingModel = ThreadingModel.Free)]
public class ThrowsWhenCreated : IPropertyPageProvider
{
    public ThrowsWhenCreated() => throw new InvalidOperationException("cannot be created");
    public IReadOnlyList<PropertyPageInfo> GetPages(PropertyTarget target) => [];
}
