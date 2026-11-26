public partial class Item
{
    public string Name { get; protected set; }
    public Location Location { get; protected set; }

    public override string ToString()
    {
        return $"Item named '{Name}' at '{Location}'";
    }
}