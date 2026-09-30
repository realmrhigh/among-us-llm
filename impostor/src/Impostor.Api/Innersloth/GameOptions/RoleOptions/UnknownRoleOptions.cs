namespace Impostor.Api.Innersloth.GameOptions.RoleOptions;

/// <summary>
///     Role options for a role type this build does not know yet. The raw bytes are kept so they can be relayed unchanged.
/// </summary>
public class UnknownRoleOptions : IRoleOptions
{
    public UnknownRoleOptions(RoleTypes type, byte[] data)
    {
        Type = type;
        Data = data;
    }

    public RoleTypes Type { get; }

    public byte[] Data { get; }

    public static UnknownRoleOptions Deserialize(IMessageReader reader, RoleTypes type)
    {
        var data = new byte[reader.Length - reader.Position];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = reader.ReadByte();
        }

        return new UnknownRoleOptions(type, data);
    }

    public void Serialize(IMessageWriter writer)
    {
        foreach (var b in Data)
        {
            writer.Write(b);
        }
    }
}
