namespace Vibe.Registry;

// What changed between two copies of a hive, as the change events a local edit would have raised.
internal static class HiveDiff
{
    public static List<RegistryChange> Between(Node before, Node after)
    {
        var changes = new List<RegistryChange>();
        Compare(before, after, changes);
        return changes;
    }

    // A reload replaces the tree, which would drop keys that only exist in this process (Volatile ones).
    // Carry them over: they were never on disk, so another process cannot have removed them.
    public static void GraftVolatile(Node before, Node into)
    {
        foreach (var child in before.Children.Values)
        {
            if ((child.Flags & RegistryKeyFlags.Volatile) != 0)
            {
                if (!into.Children.ContainsKey(child.Name))
                    into.Children.Add(child.Name, child.CloneUnder(into));
            }
            else if (into.Children.TryGetValue(child.Name, out var counterpart))
            {
                GraftVolatile(child, counterpart);
            }
        }
    }

    private static void Compare(Node before, Node after, List<RegistryChange> changes)
    {
        var path = after.FullPath;

        if (before.Flags != after.Flags)
            changes.Add(new RegistryChange(RegistryChangeKind.KeyFlagsChanged, path));
        if (before.Perms != after.Perms)
            changes.Add(new RegistryChange(RegistryChangeKind.PermissionsChanged, path));

        foreach (var value in after.Values.Values)
        {
            if (!before.Values.TryGetValue(value.Name, out var old) || !old.SameAs(value))
                changes.Add(new RegistryChange(RegistryChangeKind.ValueSet, path, value.Name));
            else if (old.Permissions != value.Permissions)
                changes.Add(new RegistryChange(RegistryChangeKind.PermissionsChanged, path, value.Name));
        }
        foreach (var value in before.Values.Values)
            if (!after.Values.ContainsKey(value.Name))
                changes.Add(new RegistryChange(RegistryChangeKind.ValueDeleted, path, value.Name));

        foreach (var child in after.Children.Values)
        {
            if (before.Children.TryGetValue(child.Name, out var old))
                Compare(old, child, changes);
            else
                Created(child, changes);
        }
        foreach (var child in before.Children.Values)
            if (!after.Children.ContainsKey(child.Name))
                changes.Add(new RegistryChange(RegistryChangeKind.KeyDeleted, child.FullPath));
    }

    private static void Created(Node node, List<RegistryChange> changes)
    {
        changes.Add(new RegistryChange(RegistryChangeKind.KeyCreated, node.FullPath));
        foreach (var value in node.Values.Values)
            changes.Add(new RegistryChange(RegistryChangeKind.ValueSet, node.FullPath, value.Name));
        foreach (var child in node.Children.Values)
            Created(child, changes);
    }
}
