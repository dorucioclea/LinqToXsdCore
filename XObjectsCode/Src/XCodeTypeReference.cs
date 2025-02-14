using System.CodeDom;

namespace Xml.Schema.Linq.CodeGen;

public class XCodeTypeReference : CodeTypeReference
{
    public string fullTypeName;

    public XCodeTypeReference(string typeName) : base(typeName)
    {
    }

    public XCodeTypeReference(string typeName, params CodeTypeReference[] typeArguments) : base(typeName,
        typeArguments)
    {
    }
}