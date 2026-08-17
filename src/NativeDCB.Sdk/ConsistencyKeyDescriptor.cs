using System.Reflection;

namespace NativeDCB.Sdk;

public sealed record ConsistencyKeyDescriptor(string Name, PropertyInfo Property)
{
    public string Encode(object instance)
    {
        return SchemaDescriptor.EncodeValue(Property.GetValue(instance));
    }
}