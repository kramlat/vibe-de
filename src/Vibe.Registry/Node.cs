namespace Vibe.Registry;

internal sealed class Node
{
    public string Name { get; }
    public Node? Parent { get; }
    public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RegistryValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public RegistryKeyFlags Flags { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime Modified { get; set; } = DateTime.UtcNow;

    public Node(string name, Node? parent)
    {
        Name = name;
        Parent = parent;
    }

    public string FullPath => Parent is null ? string.Empty
        : Parent.Parent is null ? Name
        : Parent.FullPath + "\\" + Name;
}
