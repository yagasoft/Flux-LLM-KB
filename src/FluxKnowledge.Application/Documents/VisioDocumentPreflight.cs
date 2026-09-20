using System.IO.Compression;
using System.Text;
using System.Xml;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;

namespace FluxKnowledge.Application.Documents;

/// <summary>
/// Refuses active VSDX package features before an installed Visio process is ever activated.
/// This is deliberately a narrow package safety check, not a second VSDX text parser.
/// </summary>
public static class VisioDocumentPreflight
{
    public const long MaximumInputBytes = 64L * 1024 * 1024;
    public const int MaximumPageCount = 500;
    public const int MaximumTraversalDepth = 128;
    public const int MaximumShapesPerPage = 50_000;
    public const int MaximumGroupDepth = 64;
    public const long MaximumExtractedUtf8Bytes = 16L * 1024 * 1024;
    public const int MaximumMetadataUtf8Bytes = 4 * 1024 * 1024;
    public static readonly TimeSpan MaximumRunTime = TimeSpan.FromMinutes(10);

    private const int MaximumRelationshipElements = 8_192;
    private const long MaximumRelationshipPartBytes = 4L * 1024 * 1024;
    private static readonly RetainedProcessorOptions ArchiveOptions = new()
    {
        MaximumCompressedInputBytes = MaximumInputBytes,
        MaximumEntryCount = 512,
        MaximumExpandedBytes = 128L * 1024 * 1024,
        MaximumMemberBytes = 16L * 1024 * 1024,
        MaximumLogicalPathLength = 512
    };

    public static async ValueTask ValidateAsync(RetainedSourceBytes retained, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retained);
        cancellationToken.ThrowIfCancellationRequested();
        if (retained.ByteLength != retained.Bytes.LongLength || retained.ByteLength > MaximumInputBytes)
        {
            throw new RetainedProcessorException("visio-document-input-too-large");
        }

        await ZipArchiveRetainedProcessor.ValidateSafeArchiveAsync(retained, ArchiveOptions, cancellationToken)
            .ConfigureAwait(false);
        await OoxmlStructuralTextProcessor.ValidateVsdxSemanticTextPartsUtf8Async(retained.Bytes, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            using var bytes = new MemoryStream(retained.Bytes, writable: false);
            using var archive = new ZipArchive(bytes, ZipArchiveMode.Read, leaveOpen: false);
            var pageParts = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = entry.FullName.Replace('\\', '/');
                if (IsMacroPart(path))
                {
                    throw new RetainedProcessorException("visio-document-macro-content");
                }
                if (IsActiveEmbeddedPart(path))
                {
                    throw new RetainedProcessorException("visio-document-active-embedded-content");
                }
                if (IsDataConnectionPart(path))
                {
                    throw new RetainedProcessorException("visio-document-active-data-connection");
                }
                if (IsVisioPagePart(path) && ++pageParts > MaximumPageCount)
                {
                    throw new RetainedProcessorException("visio-document-page-limit-exceeded");
                }
                if (path.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                {
                    await ValidateRelationshipsAsync(entry, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException)
        {
            throw new RetainedProcessorException("visio-document-container-invalid", innerException: exception);
        }
    }

    private static bool IsVisioPagePart(string path) =>
        path.StartsWith("visio/pages/page", StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
        !path.Contains("_rels/", StringComparison.OrdinalIgnoreCase);

    private static bool IsMacroPart(string path) =>
        path.EndsWith("vbaproject.bin", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/macros/", StringComparison.OrdinalIgnoreCase);

    // ActiveX/OLE controls and embedded package relationships can execute or refresh on open.
    // Ordinary media parts are intentionally not considered active content.
    private static bool IsActiveEmbeddedPart(string path) =>
        path.Contains("/activex/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/embeddings/", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && path.Contains("/visio/", StringComparison.OrdinalIgnoreCase);

    private static bool IsDataConnectionPart(string path) =>
        path.EndsWith("/connections.xml", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/dataconnections/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/externaldata/", StringComparison.OrdinalIgnoreCase);

    private static async ValueTask ValidateRelationshipsAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Length > MaximumRelationshipPartBytes)
        {
            throw new RetainedProcessorException("visio-document-relationship-part-too-large");
        }

        using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumRelationshipPartBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = true
        });
        var relationships = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship")
            {
                continue;
            }
            if (++relationships > MaximumRelationshipElements)
            {
                throw new RetainedProcessorException("visio-document-relationship-limit");
            }

            var targetMode = reader.GetAttribute("TargetMode");
            if (string.Equals(targetMode, "External", StringComparison.OrdinalIgnoreCase))
            {
                throw new RetainedProcessorException("visio-document-external-relationship");
            }

            var type = reader.GetAttribute("Type");
            if (string.IsNullOrWhiteSpace(type))
            {
                throw new RetainedProcessorException("visio-document-relationship-invalid");
            }
            if (IsActiveRelationship(type))
            {
                throw new RetainedProcessorException("visio-document-active-relationship");
            }
        }
    }

    private static bool IsActiveRelationship(string type) =>
        type.Contains("dataconnection", StringComparison.OrdinalIgnoreCase) ||
        type.EndsWith("/connections", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("externaldata", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("oledb", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("oleobject", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("activex", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("control", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("embedded", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("refresh", StringComparison.OrdinalIgnoreCase);
}
