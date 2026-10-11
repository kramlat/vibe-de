namespace Vibe.Registry;

internal sealed class Node
{
    public string Name { get; }
    public Node? Parent { get; }
    public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RegistryValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public RegistryKeyFlags Flags { get; set; }
    public PosixPermissions Perms { get; set; } = new(0, 0, PosixPermissions.KeyDefault);
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime Modified { get; set; } = DateTime.UtcNow;

    public Node(string name, Node? parent)
    {
        Name = name;
        Parent = parent;
    }

    // A deep copy under a new parent.
    public Node CloneUnder(Node? parent)
    {
        var copy = new Node(Name, parent) { Flags = Flags, Created = Created, Modified = Modified, Perms = Perms };
        foreach (var value in Values)
            copy.Values.Add(value.Key, value.Value);
        foreach (var child in Children.Values)
            copy.Children.Add(child.Name, child.CloneUnder(copy));
        return copy;
    }

    public string FullPath => Parent is null ? string.Empty
        : Parent.Parent is null ? Name
        : Parent.FullPath + "\\" + Name;
}
