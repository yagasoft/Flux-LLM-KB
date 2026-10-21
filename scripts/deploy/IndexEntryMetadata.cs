#nullable enable
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace FluxKnowledge.Deployment
{
    // Reads metadata only. Never load or execute a candidate to ask whether it is compatible.
    public static class IndexEntryMetadata
    {
        public static int[] Read(Stream stream, string expectedAssembly)
        {
            if (stream.Length < 1 || stream.Length > 64L * 1024 * 1024)
                throw new InvalidOperationException("index-entry-metadata-size-invalid");
            using (var pe = new PEReader(stream, PEStreamOptions.LeaveOpen))
            {
                var reader = pe.GetMetadataReader();
                var assembly = reader.GetAssemblyDefinition();
                if (reader.GetString(assembly.Name) != expectedAssembly)
                    throw new InvalidOperationException("index-entry-assembly-identity-invalid");
                string? formats = null;
                foreach (var handle in assembly.GetCustomAttributes())
                {
                    var attribute = reader.GetCustomAttribute(handle);
                    if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                    var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
                    var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                    if (reader.GetString(type.Namespace) != "System.Reflection" ||
                        reader.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
                    var value = reader.GetBlobReader(attribute.Value);
                    if (value.ReadUInt16() != 1) throw new InvalidOperationException("index-entry-metadata-invalid");
                    var key = value.ReadSerializedString();
                    var data = value.ReadSerializedString();
                    if (key != "FluxKnowledge.IndexFormats") continue;
                    if (formats != null || value.ReadUInt16() != 0 || value.RemainingBytes != 0 ||
                        data != "1" && data != "1,2")
                        throw new InvalidOperationException("index-entry-format-declaration-invalid");
                    formats = data;
                }
                return formats == null ? Array.Empty<int>() : formats == "1" ? new[] { 1 } : new[] { 1, 2 };
            }
        }
    }
}
