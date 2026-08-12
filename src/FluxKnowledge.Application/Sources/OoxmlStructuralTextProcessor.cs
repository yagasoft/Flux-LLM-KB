using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>
/// Bounded, retained-only structural extraction for Open XML packages. It neither evaluates
/// formulas nor follows relationships, links, macros, embedded objects or external resources.
/// </summary>
public sealed class OoxmlStructuralTextProcessor(IRetainedArtifactWriter artifactWriter) : ILocalSourceCapabilityHandler
{
    private const long MaximumInputBytes = 128L * 1024 * 1024;
    private const long MaximumExpandedXmlBytes = 256L * 1024 * 1024;
    private const int MaximumElements = 200_000;
    private const int MaximumDepth = 128;
    private const long MaximumTextBytes = 32L * 1024 * 1024;
    private const long MaximumChildTextBytes = 16L * 1024 * 1024;
    private const int MaximumEntries = 512;
    private const int MaximumRelationships = 8_192;
    private const long MaximumSelectedPartBytes = 32L * 1024 * 1024;
    private const int MaximumPathLength = 512;
    private const int MaximumCompressionRatio = 100;
    private const string OfficeDocumentRelationshipType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    private const string WorksheetRelationshipType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";
    private const string SlideRelationshipType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide";
    private const string SharedStringsRelationshipType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings";
    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string WordprocessingNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string PresentationNamespace = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string OfficeRelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static readonly SourceCapabilityDescriptor Capability = new(
        new Guid("3d72bf21-5358-482d-a6a9-576ff23012a3"),
        "document-ooxml-structural-extract",
        "phase-5-ooxml-structural-v1",
        ExecutionClass.InProcess,
        "phase-5-ooxml-retained-structural-v1",
        SourceActivityKind.TextExtraction,
        "OoxmlDocumentContainer",
        "retained:document-ooxml-structural-extract");

    public SourceCapabilityDescriptor Descriptor => Capability;

    /// <summary>Promotion is extension-led; package confirmation stays inside the bounded processor.</summary>
    public static bool IsLikelyOoxml(RetainedProcessorPromotionCandidate candidate, ReadOnlySpan<byte> _) =>
        candidate.Extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
        candidate.Extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
        candidate.Extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase);

    public async ValueTask<RetainedProcessorCompletion> ProcessAsync(
        RetainedProcessorClaim claim,
        RetainedSourceBytes retained,
        RetainedProcessorOptions options,
        CancellationToken cancellationToken)
    {
        if (retained.ByteLength > MaximumInputBytes || retained.Bytes.LongLength > MaximumInputBytes)
            throw new RetainedProcessorException("office-document-input-too-large");
        if (!string.Equals(retained.ContentSha256, claim.InputSha256, StringComparison.Ordinal) ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(retained.Bytes)), claim.InputSha256, StringComparison.Ordinal))
            throw new RetainedProcessorException("retained-artifact-checksum-invalid");
        if (!ZipArchiveRetainedProcessor.IsZipSignature(retained.Bytes))
        {
            if (IsEncryptedCompoundOfficeWrapper(retained.Bytes))
                throw new RetainedProcessorException("office-document-encrypted");
            throw new RetainedProcessorException("office-document-container-invalid");
        }

        try
        {
            using var input = new MemoryStream(retained.Bytes, writable: false);
            using var package = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            if (package.Entries.Count > MaximumEntries) throw new RetainedProcessorException("office-document-container-invalid");
            try { ZipArchiveRetainedProcessor.ValidateSafeCentralDirectory(retained.Bytes, MaximumEntries); }
            catch (RetainedProcessorException error) when (error.OutcomeCode == "archive-entry-encrypted") { throw new RetainedProcessorException("office-document-encrypted", innerException: error); }
            catch (RetainedProcessorException error) { throw new RetainedProcessorException("office-document-container-invalid", innerException: error); }

            var entriesByPath = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            var entriesInArchiveOrder = new List<ZipArchiveEntry>(package.Entries.Count);
            long expandedTotal = 0;
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in package.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ValidatePath(entry.FullName);
                if (!paths.Add(path) || !entriesByPath.TryAdd(path, entry)) throw new RetainedProcessorException("office-document-container-invalid");
                entriesInArchiveOrder.Add(entry);
                if (entry.Length < 0 || entry.CompressedLength < 0 || entry.Length > checked(Math.Max(1L, entry.CompressedLength) * MaximumCompressionRatio))
                    throw new RetainedProcessorException("office-document-expanded-xml-limit");
                if (IsXmlPart(path))
                {
                    if (entry.Length > MaximumSelectedPartBytes) throw new RetainedProcessorException("office-document-part-unsupported");
                    expandedTotal = checked(expandedTotal + entry.Length);
                    if (expandedTotal > MaximumExpandedXmlBytes) throw new RetainedProcessorException("office-document-expanded-xml-limit");
                }
            }
            var xmlBudget = new XmlBudget();
            foreach (var entry in entriesInArchiveOrder)
            {
                if (IsXmlPart(entry.FullName)) ScanXmlPart(entry, xmlBudget, cancellationToken);
            }
            var selected = ValidatePackageTopology(entriesByPath, cancellationToken);

            var text = new StructuralTextBuffer();
            switch (selected)
            {
                case WordTopology word:
                    await AppendStructuralTextAsync(word.Document, text, cancellationToken).ConfigureAwait(false);
                    break;
                case WorkbookTopology workbook:
                    var sharedStrings = workbook.SharedStrings is null
                        ? Array.Empty<string>()
                        : ReadSharedStrings(workbook.SharedStrings, cancellationToken);
                    foreach (var worksheet in workbook.Worksheets)
                        await AppendWorksheetTextAsync(worksheet, sharedStrings, text, cancellationToken).ConfigureAwait(false);
                    break;
                case PresentationTopology presentation:
                    foreach (var slide in presentation.Slides)
                        await AppendStructuralTextAsync(slide, text, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new RetainedProcessorException("office-document-container-invalid");
            }
            if (text.Length == 0) throw new RetainedProcessorException("office-document-part-unsupported");
            return await WriteChildrenAsync(claim, text.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (RetainedProcessorException) { throw; }
        catch (XmlException exception) { throw new RetainedProcessorException("office-document-xml-invalid", innerException: exception); }
        catch (InvalidDataException exception) { throw new RetainedProcessorException("office-document-container-invalid", innerException: exception); }
        catch (NotSupportedException exception) { throw new RetainedProcessorException("office-document-container-invalid", innerException: exception); }
    }

    private static bool IsXmlPart(string path) => path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

    // This validates only the bounded CFB header, FAT/DIFAT map and directory chain needed to recognise
    // an OOXML encryption wrapper. It deliberately does not inspect arbitrary sectors or legacy content.
    private static bool IsEncryptedCompoundOfficeWrapper(ReadOnlySpan<byte> bytes)
    {
        const int headerLength = 512;
        const uint freeSector = 0xffffffff;
        const uint endOfChain = 0xfffffffe;
        const uint fatSector = 0xfffffffdu;
        const uint difatSector = 0xfffffffcu;
        const int maximumDirectorySectors = 32;
        if (bytes.Length < headerLength || !bytes.StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }) ||
            !TryReadUInt16(bytes, 26, out var majorVersion) || majorVersion is not 3 and not 4 ||
            !TryReadUInt16(bytes, 28, out var byteOrder) || byteOrder != 0xfffe ||
            !TryReadUInt16(bytes, 30, out var sectorShift) || sectorShift != (majorVersion == 3 ? 9 : 12) ||
            !TryReadUInt16(bytes, 32, out var miniSectorShift) || miniSectorShift != 6 ||
            !bytes.Slice(34, 6).SequenceEqual(stackalloc byte[6]) ||
            !TryReadUInt32(bytes, 40, out var directorySectorCount) || majorVersion == 3 && directorySectorCount != 0 ||
            !TryReadUInt32(bytes, 44, out var fatSectorCount) || fatSectorCount == 0 ||
            !TryReadUInt32(bytes, 48, out var directorySector) || directorySector is freeSector or endOfChain ||
            !TryReadUInt32(bytes, 56, out var miniStreamCutoff) || miniStreamCutoff != 4096 ||
            !TryReadUInt32(bytes, 60, out var firstMiniFatSector) ||
            !TryReadUInt32(bytes, 64, out var miniFatSectorCount) ||
            !TryReadUInt32(bytes, 68, out var firstDifatSector) ||
            !TryReadUInt32(bytes, 72, out var difatSectorCount))
        {
            return false;
        }

        if (!bytes.Slice(8, 16).SequenceEqual(stackalloc byte[16])) return false;
        var sectorSize = 1 << sectorShift;
        var headerRegionSize = majorVersion == 4 ? sectorSize : headerLength;
        if (bytes.Length < headerRegionSize + sectorSize || (bytes.Length - headerRegionSize) % sectorSize != 0 ||
            majorVersion == 4 && !IsAllZero(bytes.Slice(headerLength, sectorSize - headerLength)))
        {
            return false;
        }
        var availableSectors = (bytes.Length - headerRegionSize) / sectorSize;
        var fatEntriesPerSector = sectorSize / 4;
        if (fatSectorCount > (uint)availableSectors || directorySector >= (uint)availableSectors ||
            (long)fatSectorCount * fatEntriesPerSector < availableSectors ||
            miniFatSectorCount > (uint)availableSectors ||
            miniFatSectorCount == 0 && firstMiniFatSector != endOfChain ||
            miniFatSectorCount != 0 && (firstMiniFatSector is freeSector or endOfChain || firstMiniFatSector >= availableSectors) ||
            majorVersion == 4 && (directorySectorCount == 0 || directorySectorCount > (uint)availableSectors))
        {
            return false;
        }

        var fatSectors = new List<uint>(checked((int)fatSectorCount));
        var fatSectorSet = new HashSet<uint>();
        for (var index = 0; index < Math.Min(109, checked((int)fatSectorCount)); index++)
        {
            if (!TryReadUInt32(bytes, 76 + (index * 4), out var sector) || sector is freeSector or endOfChain || sector >= availableSectors || !fatSectorSet.Add(sector))
                return false;
            fatSectors.Add(sector);
        }
        for (var index = fatSectors.Count; index < 109; index++)
        {
            if (!TryReadUInt32(bytes, 76 + (index * 4), out var sector) || sector != freeSector)
                return false;
        }

        var remainingFatSectors = checked((int)fatSectorCount) - fatSectors.Count;
        var difatEntriesPerSector = fatEntriesPerSector - 1;
        var requiredDifatSectors = remainingFatSectors == 0 ? 0 : (remainingFatSectors + difatEntriesPerSector - 1) / difatEntriesPerSector;
        if (difatSectorCount != requiredDifatSectors ||
            difatSectorCount == 0 && firstDifatSector != endOfChain ||
            difatSectorCount != 0 && (firstDifatSector is freeSector or endOfChain || firstDifatSector >= availableSectors))
        {
            return false;
        }

        var difatSectors = new HashSet<uint>();
        var nextDifatSector = firstDifatSector;
        for (var difatIndex = 0; difatIndex < requiredDifatSectors; difatIndex++)
        {
            if (nextDifatSector >= availableSectors || !difatSectors.Add(nextDifatSector) || fatSectors.Contains(nextDifatSector) ||
                !TryGetCfbSectorOffset(bytes, sectorSize, availableSectors, nextDifatSector, out var difatOffset))
            {
                return false;
            }
            var difat = bytes.Slice(difatOffset, sectorSize);
            for (var slot = 0; slot < difatEntriesPerSector; slot++)
            {
                if (!TryReadUInt32(difat, slot * 4, out var sector)) return false;
                if (fatSectors.Count < fatSectorCount)
                {
                    if (sector is freeSector or endOfChain || sector >= availableSectors || difatSectors.Contains(sector) || !fatSectorSet.Add(sector)) return false;
                    fatSectors.Add(sector);
                }
                else if (sector != freeSector)
                {
                    return false;
                }
            }
            if (!TryReadUInt32(difat, difatEntriesPerSector * 4, out nextDifatSector)) return false;
            if (difatIndex + 1 == requiredDifatSectors)
            {
                if (nextDifatSector != endOfChain) return false;
            }
            else if (nextDifatSector is freeSector or endOfChain || nextDifatSector >= availableSectors)
            {
                return false;
            }
        }
        if (fatSectors.Count != fatSectorCount) return false;
        foreach (var sector in fatSectors)
        {
            if (!TryReadFatSectorValue(bytes, sectorSize, availableSectors, fatSectors, sector, out var marker) || marker != fatSector) return false;
        }
        foreach (var sector in difatSectors)
        {
            if (!TryReadFatSectorValue(bytes, sectorSize, availableSectors, fatSectors, sector, out var marker) || marker != difatSector) return false;
        }
        for (var sector = availableSectors; sector < fatSectors.Count * fatEntriesPerSector; sector++)
        {
            if (!TryReadFatSectorValue(bytes, sectorSize, availableSectors, fatSectors, (uint)sector, out var marker) || marker != freeSector) return false;
        }

        var visited = new HashSet<uint>();
        var directorySectors = new HashSet<uint>();
        var directoryEntries = new List<CfbDirectoryEntry?>();
        var directoryLimit = majorVersion == 4
            ? Math.Min(checked((int)directorySectorCount), maximumDirectorySectors)
            : Math.Min(availableSectors, maximumDirectorySectors);
        for (var directoryCount = 0; directoryCount < directoryLimit; directoryCount++)
        {
            if (directorySector == endOfChain) break;
            if (directorySector == freeSector || directorySector >= availableSectors || !visited.Add(directorySector) ||
                fatSectorSet.Contains(directorySector) || difatSectors.Contains(directorySector) ||
                !TryGetCfbSectorOffset(bytes, sectorSize, availableSectors, directorySector, out var directoryOffset))
            {
                return false;
            }
            directorySectors.Add(directorySector);
            for (var offset = 0; offset < sectorSize; offset += 128)
            {
                var directoryEntry = bytes.Slice(directoryOffset + offset, 128);
                if (!TryReadCfbDirectoryEntry(directoryEntry, majorVersion, directoryEntries.Count == 0, out var item)) return false;
                directoryEntries.Add(item);
            }
            if (!TryReadFatSectorValue(bytes, sectorSize, availableSectors, fatSectors, directorySector, out directorySector)) return false;
        }
        if (directorySector != endOfChain || visited.Count == 0 || visited.Count > maximumDirectorySectors || majorVersion == 4 && visited.Count != directorySectorCount) return false;

        var miniFatSectors = new List<uint>(checked((int)miniFatSectorCount));
        var miniFatSectorSet = new HashSet<uint>();
        var nextMiniFatSector = firstMiniFatSector;
        for (var index = 0; index < miniFatSectorCount; index++)
        {
            if (nextMiniFatSector is freeSector or endOfChain || nextMiniFatSector >= availableSectors ||
                !miniFatSectorSet.Add(nextMiniFatSector) || fatSectorSet.Contains(nextMiniFatSector) ||
                difatSectors.Contains(nextMiniFatSector) || directorySectors.Contains(nextMiniFatSector))
            {
                return false;
            }
            miniFatSectors.Add(nextMiniFatSector);
            if (!TryReadFatSectorValue(bytes, sectorSize, availableSectors, fatSectors, nextMiniFatSector, out nextMiniFatSector)) return false;
        }
        if (nextMiniFatSector != endOfChain ||
            directoryEntries[0] is not { ObjectType: 5 } root)
        {
            return false;
        }

        var allocatedRegularSectors = new HashSet<uint>();
        foreach (var sector in fatSectorSet)
        {
            if (!allocatedRegularSectors.Add(sector)) return false;
        }
        foreach (var sector in difatSectors)
        {
            if (!allocatedRegularSectors.Add(sector)) return false;
        }
        foreach (var sector in directorySectors)
        {
            if (!allocatedRegularSectors.Add(sector)) return false;
        }
        foreach (var sector in miniFatSectors)
        {
            if (!allocatedRegularSectors.Add(sector)) return false;
        }

        var allocatedMiniSectors = new HashSet<uint>();
        ulong miniStreamSectorCapacity = 0;
        if (root.StreamSize > 0 &&
            (!TryValidateRegularCfbStreamAllocation(bytes, sectorSize, availableSectors, fatSectors, root, allocatedRegularSectors) ||
             !TryGetCfbAllocationCount(root.StreamSize, 1 << miniSectorShift, out miniStreamSectorCapacity)))
        {
            return false;
        }

        if (!TryGetEncryptedOfficeWrapperStreams(directoryEntries, out _, out var encryptionInfo, out var encryptedPackage) ||
            encryptionInfo is null || encryptedPackage is null ||
            encryptionInfo.StreamSize == 0 || encryptedPackage.StreamSize == 0)
        {
            return false;
        }

        var hasMiniStream = encryptionInfo.StreamSize < miniStreamCutoff || encryptedPackage.StreamSize < miniStreamCutoff;
        if (hasMiniStream && (miniFatSectors.Count == 0 || root.StreamSize == 0))
        {
            return false;
        }

        var miniFatEntryCapacity = (ulong)miniFatSectors.Count * (uint)fatEntriesPerSector;
        if (encryptionInfo.StreamSize < miniStreamCutoff)
        {
            if (!TryValidateMiniCfbStreamAllocation(bytes, sectorSize, availableSectors, miniFatSectors, fatEntriesPerSector, miniFatEntryCapacity,
                miniStreamSectorCapacity, encryptionInfo, allocatedMiniSectors)) return false;
        }
        else if (!TryValidateRegularCfbStreamAllocation(bytes, sectorSize, availableSectors, fatSectors, encryptionInfo, allocatedRegularSectors))
        {
            return false;
        }

        return encryptedPackage.StreamSize < miniStreamCutoff
            ? TryValidateMiniCfbStreamAllocation(bytes, sectorSize, availableSectors, miniFatSectors, fatEntriesPerSector, miniFatEntryCapacity,
                miniStreamSectorCapacity, encryptedPackage, allocatedMiniSectors)
            : TryValidateRegularCfbStreamAllocation(bytes, sectorSize, availableSectors, fatSectors, encryptedPackage, allocatedRegularSectors);
    }

    private static bool TryReadCfbDirectoryEntry(ReadOnlySpan<byte> entry, ushort majorVersion, bool isFirstEntry, out CfbDirectoryEntry? result)
    {
        result = null;
        var objectType = entry[66];
        if (objectType == 0) return !isFirstEntry && IsValidFreeCfbDirectoryEntry(entry);
        if (objectType is not 1 and not 2 and not 5 || entry[67] is not 0 and not 1 ||
            !TryReadUInt16(entry, 64, out var nameLength) || nameLength is < 2 or > 64 || (nameLength & 1) != 0 ||
            entry[nameLength - 2] != 0 || entry[nameLength - 1] != 0)
        {
            return false;
        }
        var name = IdentifyCfbDirectoryName(entry[..(nameLength - 2)]);
        if (isFirstEntry != (objectType == 5 && name == CfbDirectoryName.RootEntry)) return false;
        if (!isFirstEntry && objectType == 5) return false;
        if (!TryReadUInt32(entry, 68, out var leftSibling) || !TryReadUInt32(entry, 72, out var rightSibling) || !TryReadUInt32(entry, 76, out var child) ||
            !TryReadUInt32(entry, 116, out var startSector) || !TryReadUInt64(entry, 120, out var streamSize) ||
            objectType == 2 && child != 0xffffffff)
        {
            return false;
        }
        if (majorVersion == 3) streamSize &= uint.MaxValue;
        result = new CfbDirectoryEntry(objectType, name, leftSibling, rightSibling, child, startSector, streamSize);
        return true;
    }

    private static bool TryGetEncryptedOfficeWrapperStreams(
        IReadOnlyList<CfbDirectoryEntry?> entries,
        out CfbDirectoryEntry? root,
        out CfbDirectoryEntry? encryptionInfo,
        out CfbDirectoryEntry? encryptedPackage)
    {
        root = null;
        encryptionInfo = null;
        encryptedPackage = null;
        if (entries.Count == 0 || entries[0] is not { ObjectType: 5 } rootEntry || rootEntry.Child == 0xffffffff) return false;
        root = rootEntry;
        var seen = new HashSet<uint>();
        var pending = new Stack<(uint EntryId, bool IsDataSpacesDescendant)>();
        pending.Push((rootEntry.Child, false));
        while (pending.TryPop(out var candidate))
        {
            var (entryId, isDataSpacesDescendant) = candidate;
            if (entryId >= (uint)entries.Count || !seen.Add(entryId) || entries[(int)entryId] is not { } entry) return false;
            if (entry.Name == CfbDirectoryName.DataSpaces)
            {
                if (entry.ObjectType != 1) return false;
            }
            else if (entry.ObjectType == 1)
            {
                if (!isDataSpacesDescendant) return false;
            }
            else if (entry.ObjectType != 2)
            {
                return false;
            }
            if (!isDataSpacesDescendant)
            {
                if (entry.Name == CfbDirectoryName.EncryptionInfo)
                {
                    if (encryptionInfo is not null) return false;
                    encryptionInfo = entry;
                }
                else if (entry.Name == CfbDirectoryName.EncryptedPackage)
                {
                    if (encryptedPackage is not null) return false;
                    encryptedPackage = entry;
                }
            }
            if (entry.LeftSibling != 0xffffffff) pending.Push((entry.LeftSibling, isDataSpacesDescendant));
            if (entry.RightSibling != 0xffffffff) pending.Push((entry.RightSibling, isDataSpacesDescendant));
            if (entry.ObjectType == 1 && entry.Child != 0xffffffff) pending.Push((entry.Child, true));
        }
        return encryptionInfo is not null && encryptedPackage is not null;
    }

    private static CfbDirectoryName IdentifyCfbDirectoryName(ReadOnlySpan<byte> value) =>
        IsCfbUnicodeName(value, "Root Entry") ? CfbDirectoryName.RootEntry :
        IsCfbUnicodeName(value, "EncryptionInfo") ? CfbDirectoryName.EncryptionInfo :
        IsCfbUnicodeName(value, "EncryptedPackage") ? CfbDirectoryName.EncryptedPackage :
        IsCfbUnicodeName(value, "\u0006DataSpaces") ? CfbDirectoryName.DataSpaces : CfbDirectoryName.Other;

    private static bool IsValidFreeCfbDirectoryEntry(ReadOnlySpan<byte> entry)
    {
        if (!TryReadUInt32(entry, 68, out var left) || !TryReadUInt32(entry, 72, out var right) || !TryReadUInt32(entry, 76, out var child) ||
            left != 0xffffffff || right != 0xffffffff || child != 0xffffffff)
        {
            return false;
        }
        for (var index = 0; index < entry.Length; index++)
        {
            if (index is >= 68 and < 80) continue;
            if (entry[index] != 0) return false;
        }
        return true;
    }

    private static bool IsCfbUnicodeName(ReadOnlySpan<byte> value, string expected)
    {
        if (value.Length != expected.Length * 2) return false;
        for (var index = 0; index < expected.Length; index++)
        {
            if (value[index * 2] != expected[index] || value[(index * 2) + 1] != 0) return false;
        }
        return true;
    }

    private static bool TryGetCfbSectorOffset(ReadOnlySpan<byte> bytes, int sectorSize, int availableSectors, uint sector, out int offset)
    {
        offset = 0;
        if (sector >= availableSectors) return false;
        var candidate = (long)sectorSize + ((long)sector * sectorSize);
        if (candidate > bytes.Length - sectorSize) return false;
        offset = (int)candidate;
        return true;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> value)
    {
        foreach (var item in value)
        {
            if (item != 0) return false;
        }
        return true;
    }

    private sealed record CfbDirectoryEntry(
        byte ObjectType,
        CfbDirectoryName Name,
        uint LeftSibling,
        uint RightSibling,
        uint Child,
        uint StartSector,
        ulong StreamSize);

    private enum CfbDirectoryName { Other, RootEntry, EncryptionInfo, EncryptedPackage, DataSpaces }

    private static bool TryValidateRegularCfbStreamAllocation(
        ReadOnlySpan<byte> bytes,
        int sectorSize,
        int availableSectors,
        IReadOnlyList<uint> fatSectors,
        CfbDirectoryEntry stream,
        ISet<uint> allocatedSectors)
    {
        if (!TryGetCfbAllocationCount(stream.StreamSize, sectorSize, out var sectorCount) || sectorCount > (ulong)availableSectors)
            return false;
        if (sectorCount == 0) return stream.StartSector == 0xfffffffe;

        var currentSector = stream.StartSector;
        for (ulong index = 0; index < sectorCount; index++)
        {
            if (currentSector >= availableSectors || !allocatedSectors.Add(currentSector) ||
                !TryReadFatSectorValue(bytes, sectorSize, availableSectors, fatSectors, currentSector, out var nextSector))
            {
                return false;
            }
            if (index + 1 == sectorCount)
            {
                if (nextSector != 0xfffffffe) return false;
            }
            else if (nextSector is 0xffffffff or 0xfffffffe or 0xfffffffd or 0xfffffffcu || nextSector >= availableSectors)
            {
                return false;
            }
            else
            {
                currentSector = nextSector;
            }
        }
        return true;
    }

    private static bool TryValidateMiniCfbStreamAllocation(
        ReadOnlySpan<byte> bytes,
        int sectorSize,
        int availableSectors,
        IReadOnlyList<uint> miniFatSectors,
        int miniFatEntriesPerSector,
        ulong miniFatEntryCapacity,
        ulong miniStreamSectorCapacity,
        CfbDirectoryEntry stream,
        ISet<uint> allocatedMiniSectors)
    {
        if (!TryGetCfbAllocationCount(stream.StreamSize, 64, out var sectorCount) ||
            sectorCount > miniFatEntryCapacity || sectorCount > miniStreamSectorCapacity)
        {
            return false;
        }
        if (sectorCount == 0) return stream.StartSector == 0xfffffffe;

        var currentSector = stream.StartSector;
        for (ulong index = 0; index < sectorCount; index++)
        {
            if (currentSector >= miniFatEntryCapacity || currentSector >= miniStreamSectorCapacity ||
                !allocatedMiniSectors.Add(currentSector) ||
                !TryReadMiniFatSectorValue(bytes, sectorSize, availableSectors, miniFatSectors, miniFatEntriesPerSector, currentSector, out var nextSector))
            {
                return false;
            }
            if (index + 1 == sectorCount)
            {
                if (nextSector != 0xfffffffe) return false;
            }
            else if (nextSector is 0xffffffff or 0xfffffffe or 0xfffffffd or 0xfffffffcu ||
                nextSector >= miniFatEntryCapacity || nextSector >= miniStreamSectorCapacity)
            {
                return false;
            }
            else
            {
                currentSector = nextSector;
            }
        }
        return true;
    }

    private static bool TryGetCfbAllocationCount(ulong streamSize, int allocationUnitSize, out ulong allocationCount)
    {
        allocationCount = 0;
        if (allocationUnitSize <= 0) return false;
        if (streamSize == 0) return true;
        var allocationUnit = (ulong)(uint)allocationUnitSize;
        allocationCount = (streamSize / allocationUnit) + (streamSize % allocationUnit == 0 ? 0UL : 1UL);
        return true;
    }

    private static bool TryReadMiniFatSectorValue(
        ReadOnlySpan<byte> bytes,
        int sectorSize,
        int availableSectors,
        IReadOnlyList<uint> miniFatSectors,
        int entriesPerSector,
        uint miniSector,
        out uint value)
    {
        value = 0;
        var miniFatIndex = miniSector / (uint)entriesPerSector;
        if (miniFatIndex >= (uint)miniFatSectors.Count ||
            !TryGetCfbSectorOffset(bytes, sectorSize, availableSectors, miniFatSectors[(int)miniFatIndex], out var miniFatOffset))
        {
            return false;
        }
        return TryReadUInt32(bytes, miniFatOffset + ((int)(miniSector % (uint)entriesPerSector) * 4), out value);
    }

    private static bool TryReadFatSectorValue(ReadOnlySpan<byte> bytes, int sectorSize, int availableSectors, IReadOnlyList<uint> fatSectors, uint sector, out uint value)
    {
        value = 0;
        var entriesPerSector = sectorSize / 4;
        var fatIndex = sector / (uint)entriesPerSector;
        if (fatIndex >= (uint)fatSectors.Count ||
            !TryGetCfbSectorOffset(bytes, sectorSize, availableSectors, fatSectors[(int)fatIndex], out var fatOffset))
        {
            return false;
        }
        var offset = fatOffset + (int)(sector % (uint)entriesPerSector) * 4;
        return TryReadUInt32(bytes, offset, out value);
    }

    private static bool TryReadUInt16(ReadOnlySpan<byte> bytes, int offset, out ushort value)
    {
        value = 0;
        if (offset < 0 || offset > bytes.Length - 2) return false;
        value = (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        return true;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> bytes, int offset, out uint value)
    {
        value = 0;
        if (offset < 0 || offset > bytes.Length - 4) return false;
        value = (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
        return true;
    }

    private static bool TryReadUInt64(ReadOnlySpan<byte> bytes, int offset, out ulong value)
    {
        value = 0;
        if (offset < 0 || offset > bytes.Length - 8) return false;
        for (var index = 0; index < 8; index++) value |= (ulong)bytes[offset + index] << (index * 8);
        return true;
    }

    private static OoxmlTopology ValidatePackageTopology(
        IReadOnlyDictionary<string, ZipArchiveEntry> entriesByPath,
        CancellationToken cancellationToken)
    {
        if (!entriesByPath.TryGetValue("[Content_Types].xml", out var contentTypes) ||
            !entriesByPath.TryGetValue("_rels/.rels", out var rootRelationships))
            throw new RetainedProcessorException("office-document-container-invalid");
        string? mainPart = null;
        OoxmlFamily? family = null;
        var partContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = CreateReader(contentTypes.Open());
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Depth == 0 && (reader.LocalName != "Types" || reader.NamespaceURI != ContentTypesNamespace))
                throw new RetainedProcessorException("office-document-container-invalid");
            if (reader.LocalName == "Override")
            {
                if (reader.NamespaceURI != ContentTypesNamespace) throw new RetainedProcessorException("office-document-container-invalid");
                var partName = reader.GetAttribute("PartName") ?? string.Empty;
                var contentType = reader.GetAttribute("ContentType") ?? string.Empty;
                if (partName.Length < 2 || partName[0] != '/' || !partContentTypes.TryAdd(partName[1..], contentType))
                    throw new RetainedProcessorException("office-document-container-invalid");
                var candidate = MainPartFor(partName, contentType);
                if (candidate is not null)
                {
                    if (mainPart is not null && !string.Equals(mainPart, partName[1..], StringComparison.OrdinalIgnoreCase))
                        throw new RetainedProcessorException("office-document-container-invalid");
                    mainPart = partName[1..];
                    family = candidate.Value;
                }
            }
        }
        if (mainPart is null || !entriesByPath.ContainsKey(mainPart)) throw new RetainedProcessorException("office-document-container-invalid");
        if (family is null) throw new RetainedProcessorException("office-document-container-invalid");
        var rootTargets = ReadRelationshipTargets(rootRelationships, "", cancellationToken);
        if (!rootTargets.Values.Any(target => target.Type == OfficeDocumentRelationshipType &&
            string.Equals(target.Target, mainPart, StringComparison.OrdinalIgnoreCase)))
            throw new RetainedProcessorException("office-document-container-invalid");

        if (family == OoxmlFamily.Word)
        {
            RequirePartRoot(entriesByPath[mainPart], "document", WordprocessingNamespace, cancellationToken);
            return new WordTopology(entriesByPath[mainPart]);
        }
        if (family == OoxmlFamily.Workbook)
            return ResolveWorkbookParts(entriesByPath, partContentTypes, cancellationToken);
        if (family == OoxmlFamily.Presentation)
            return ResolvePresentationParts(entriesByPath, partContentTypes, cancellationToken);
        throw new RetainedProcessorException("office-document-container-invalid");
    }

    private static WorkbookTopology ResolveWorkbookParts(
        IReadOnlyDictionary<string, ZipArchiveEntry> entriesByPath,
        IReadOnlyDictionary<string, string> contentTypes,
        CancellationToken cancellationToken)
    {
        if (!entriesByPath.TryGetValue("xl/workbook.xml", out var workbook) ||
            !entriesByPath.TryGetValue("xl/_rels/workbook.xml.rels", out var relationships))
            throw new RetainedProcessorException("office-document-container-invalid");
        RequirePartRoot(workbook, "workbook", SpreadsheetNamespace, cancellationToken);
        var targets = ReadRelationshipTargets(relationships, "xl/workbook.xml", cancellationToken);
        var parts = new List<ZipArchiveEntry>();
        entriesByPath.TryGetValue("xl/sharedStrings.xml", out var sharedStrings);
        if (sharedStrings is not null)
        {
            if (!targets.Values.Any(target => target.Type == SharedStringsRelationshipType && target.Target == "xl/sharedStrings.xml") ||
                !contentTypes.TryGetValue("xl/sharedStrings.xml", out var sharedStringsType) ||
                sharedStringsType != "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml")
                throw new RetainedProcessorException("office-document-container-invalid");
            RequirePartRoot(sharedStrings, "sst", SpreadsheetNamespace, cancellationToken);
        }
        foreach (var relationshipId in ReadRelationshipIds(workbook, "sheet", SpreadsheetNamespace, cancellationToken))
        {
            if (!targets.TryGetValue(relationshipId, out var target) || target.Type != WorksheetRelationshipType ||
                !target.Target.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) ||
                !target.Target.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                !entriesByPath.TryGetValue(target.Target, out var worksheet) ||
                !contentTypes.TryGetValue(target.Target, out var worksheetType) ||
                worksheetType != "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")
                throw new RetainedProcessorException("office-document-container-invalid");
            RequirePartRoot(worksheet, "worksheet", SpreadsheetNamespace, cancellationToken);
            parts.Add(worksheet);
        }
        return new WorkbookTopology(sharedStrings, parts);
    }

    private static PresentationTopology ResolvePresentationParts(
        IReadOnlyDictionary<string, ZipArchiveEntry> entriesByPath,
        IReadOnlyDictionary<string, string> contentTypes,
        CancellationToken cancellationToken)
    {
        if (!entriesByPath.TryGetValue("ppt/presentation.xml", out var presentation) ||
            !entriesByPath.TryGetValue("ppt/_rels/presentation.xml.rels", out var relationships))
            throw new RetainedProcessorException("office-document-container-invalid");
        RequirePartRoot(presentation, "presentation", PresentationNamespace, cancellationToken);
        var targets = ReadRelationshipTargets(relationships, "ppt/presentation.xml", cancellationToken);
        var parts = new List<ZipArchiveEntry>();
        foreach (var relationshipId in ReadRelationshipIds(presentation, "sldId", PresentationNamespace, cancellationToken))
        {
            if (!targets.TryGetValue(relationshipId, out var target) || target.Type != SlideRelationshipType ||
                !target.Target.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) ||
                !target.Target.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                !entriesByPath.TryGetValue(target.Target, out var slide) ||
                !contentTypes.TryGetValue(target.Target, out var slideType) ||
                slideType != "application/vnd.openxmlformats-officedocument.presentationml.slide+xml")
                throw new RetainedProcessorException("office-document-container-invalid");
            RequirePartRoot(slide, "sld", PresentationNamespace, cancellationToken);
            parts.Add(slide);
        }
        return new PresentationTopology(parts);
    }

    private static Dictionary<string, RelationshipTarget> ReadRelationshipTargets(ZipArchiveEntry relationshipsEntry, string ownerPart, CancellationToken cancellationToken)
    {
        var targets = new Dictionary<string, RelationshipTarget>(StringComparer.Ordinal);
        using var relationships = CreateReader(relationshipsEntry.Open());
        while (relationships.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (relationships.NodeType != XmlNodeType.Element) continue;
            if (relationships.Depth == 0 && (relationships.LocalName != "Relationships" || relationships.NamespaceURI != RelationshipsNamespace))
                throw new RetainedProcessorException("office-document-container-invalid");
            if (relationships.LocalName != "Relationship") continue;
            if (relationships.NamespaceURI != RelationshipsNamespace) throw new RetainedProcessorException("office-document-container-invalid");
            if (string.Equals(relationships.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                throw new RetainedProcessorException("office-document-part-unsupported");
            var id = relationships.GetAttribute("Id");
            var target = relationships.GetAttribute("Target");
            var type = relationships.GetAttribute("Type");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(type) ||
                !targets.TryAdd(id, new RelationshipTarget(ResolveRelationshipTarget(ownerPart, target), type)))
                throw new RetainedProcessorException("office-document-container-invalid");
        }
        return targets;
    }

    private static IReadOnlyList<string> ReadRelationshipIds(ZipArchiveEntry part, string elementName, string partNamespace, CancellationToken cancellationToken)
    {
        var identifiers = new List<string>();
        using var reader = CreateReader(part.Open());
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || !string.Equals(reader.LocalName, elementName, StringComparison.Ordinal) || reader.NamespaceURI != partNamespace) continue;
            var id = reader.GetAttribute("id", OfficeRelationshipNamespace);
            if (string.IsNullOrWhiteSpace(id)) throw new RetainedProcessorException("office-document-container-invalid");
            identifiers.Add(id);
        }
        return identifiers;
    }

    private static void RequirePartRoot(ZipArchiveEntry entry, string localName, string namespaceUri, CancellationToken cancellationToken)
    {
        using var reader = CreateReader(entry.Open());
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Depth != 0 || reader.LocalName != localName || reader.NamespaceURI != namespaceUri)
                throw new RetainedProcessorException("office-document-container-invalid");
            return;
        }
        throw new RetainedProcessorException("office-document-container-invalid");
    }

    private static OoxmlFamily? MainPartFor(string partName, string contentType) => (partName, contentType) switch
    {
        ("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml") => OoxmlFamily.Word,
        ("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml") => OoxmlFamily.Workbook,
        ("/ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml") => OoxmlFamily.Presentation,
        _ => null
    };

    private static void ScanXmlPart(ZipArchiveEntry entry, XmlBudget budget, CancellationToken cancellationToken)
    {
        using var reader = CreateReader(entry.Open());
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element)
            {
                budget.AddElement(reader.Depth + 1);
                if (entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(reader.LocalName, "Relationship", StringComparison.Ordinal))
                {
                    if (string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                        throw new RetainedProcessorException("office-document-part-unsupported");
                    budget.AddRelationship();
                }
            }
        }
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        using var reader = CreateReader(entry.Open());
        StringBuilder? current = null;
        var stringDepth = -1;
        var textDepths = new Stack<int>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "si" && reader.NamespaceURI == SpreadsheetNamespace)
                {
                    if (current is not null) throw new RetainedProcessorException("office-document-container-invalid");
                    current = new StringBuilder();
                    stringDepth = reader.Depth;
                }
                else if (current is not null && reader.LocalName == "t" && reader.NamespaceURI == SpreadsheetNamespace)
                {
                    textDepths.Push(reader.Depth);
                }
            }
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
            {
                if (current is not null && textDepths.Count > 0) current.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (textDepths.Count > 0 && textDepths.Peek() == reader.Depth) textDepths.Pop();
                if (current is not null && reader.Depth == stringDepth && reader.LocalName == "si" && reader.NamespaceURI == SpreadsheetNamespace)
                {
                    values.Add(current.ToString());
                    current = null;
                    stringDepth = -1;
                }
            }
        }
        if (current is not null) throw new RetainedProcessorException("office-document-xml-invalid");
        return values;
    }

    private static string ResolveRelationshipTarget(string ownerPart, string target)
    {
        if (target.IndexOf('\\') >= 0 || target.IndexOf('\0') >= 0 || target.StartsWith("/", StringComparison.Ordinal) || target.Contains(':'))
            throw new RetainedProcessorException("office-document-container-invalid");
        var ownerSegments = string.IsNullOrEmpty(ownerPart) ? [] : ownerPart.Split('/').SkipLast(1).ToList();
        foreach (var segment in target.Split('/', StringSplitOptions.None))
        {
            if (segment is "" or "." or "..") throw new RetainedProcessorException("office-document-container-invalid");
            ownerSegments.Add(segment);
        }
        return string.Join('/', ownerSegments);
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength || path.IndexOf('\0') >= 0 ||
            path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("\\", StringComparison.Ordinal) || path.Contains('\\') || path.Contains(':'))
            throw new RetainedProcessorException("office-document-container-invalid");
        var parts = path.Split('/', StringSplitOptions.None);
        if (parts.Any(part => part is "" or "." or "..")) throw new RetainedProcessorException("office-document-container-invalid");
        return string.Join('/', parts);
    }

    private static async ValueTask AppendStructuralTextAsync(ZipArchiveEntry entry, StructuralTextBuffer text, CancellationToken cancellationToken)
    {
        using var reader = CreateReader(entry.Open());
        var elements = 0;
        var structuralDepths = new Stack<int>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (++elements > MaximumElements) throw new RetainedProcessorException("office-document-element-limit");
                if (reader.Depth + 1 > MaximumDepth) throw new RetainedProcessorException("office-document-depth-limit");
                if (IsStructuralTextElement(entry.FullName, reader.LocalName, reader.NamespaceURI)) structuralDepths.Push(reader.Depth);
            }
            else if ((reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA) && structuralDepths.Count > 0)
            {
                text.Append(reader.Value);
                text.Append("\n");
            }
            else if (reader.NodeType == XmlNodeType.EndElement && structuralDepths.Count > 0 && structuralDepths.Peek() == reader.Depth)
            {
                structuralDepths.Pop();
            }
        }
    }

    private static async ValueTask AppendWorksheetTextAsync(
        ZipArchiveEntry entry,
        IReadOnlyList<string> sharedStrings,
        StructuralTextBuffer text,
        CancellationToken cancellationToken)
    {
        using var reader = CreateReader(entry.Open());
        var cellDepth = -1;
        var isSharedStringCell = false;
        var valueDepth = -1;
        StringBuilder? value = null;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "c" && reader.NamespaceURI == SpreadsheetNamespace)
                {
                    if (cellDepth >= 0) throw new RetainedProcessorException("office-document-container-invalid");
                    cellDepth = reader.Depth;
                    isSharedStringCell = string.Equals(reader.GetAttribute("t"), "s", StringComparison.Ordinal);
                }
                else if (cellDepth >= 0 && reader.LocalName == "v" && reader.NamespaceURI == SpreadsheetNamespace)
                {
                    if (value is not null) throw new RetainedProcessorException("office-document-container-invalid");
                    value = new StringBuilder();
                    valueDepth = reader.Depth;
                }
            }
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
            {
                if (value is not null) value.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (value is not null && reader.Depth == valueDepth && reader.LocalName == "v" && reader.NamespaceURI == SpreadsheetNamespace)
                {
                    var raw = value.ToString();
                    if (isSharedStringCell)
                    {
                        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= sharedStrings.Count)
                            throw new RetainedProcessorException("office-document-container-invalid");
                        text.Append(sharedStrings[index]);
                    }
                    else
                    {
                        text.Append(raw);
                    }
                    text.Append("\n");
                    value = null;
                    valueDepth = -1;
                }
                if (cellDepth >= 0 && reader.Depth == cellDepth && reader.LocalName == "c")
                {
                    if (value is not null) throw new RetainedProcessorException("office-document-xml-invalid");
                    cellDepth = -1;
                    isSharedStringCell = false;
                }
            }
        }
        if (cellDepth >= 0 || value is not null) throw new RetainedProcessorException("office-document-xml-invalid");
    }

    private static bool IsStructuralTextElement(string partPath, string localName, string namespaceUri)
    {
        return (partPath.StartsWith("word/", StringComparison.OrdinalIgnoreCase) && namespaceUri == WordprocessingNamespace ||
            partPath.StartsWith("ppt/slides/", StringComparison.OrdinalIgnoreCase) && namespaceUri == PresentationNamespace) && localName == "t";
    }

    private sealed class StructuralTextBuffer
    {
        private readonly StringBuilder _value = new();
        private long _utf8Length;
        public int Length => _value.Length;
        public string Value => _value.ToString();
        public void Append(string value)
        {
            _utf8Length = checked(_utf8Length + Encoding.UTF8.GetByteCount(value));
            if (_utf8Length > MaximumTextBytes) throw new RetainedProcessorException("office-document-text-limit");
            _value.Append(value);
        }
    }

    private sealed class XmlBudget
    {
        private int _elements;
        private int _relationships;

        public void AddElement(int depth)
        {
            if (++_elements > MaximumElements) throw new RetainedProcessorException("office-document-element-limit");
            if (depth > MaximumDepth) throw new RetainedProcessorException("office-document-depth-limit");
        }

        public void AddRelationship()
        {
            if (++_relationships > MaximumRelationships) throw new RetainedProcessorException("office-document-part-unsupported");
        }
    }

    private enum OoxmlFamily { Word, Workbook, Presentation }

    private abstract record OoxmlTopology;
    private sealed record WordTopology(ZipArchiveEntry Document) : OoxmlTopology;
    private sealed record WorkbookTopology(ZipArchiveEntry? SharedStrings, IReadOnlyList<ZipArchiveEntry> Worksheets) : OoxmlTopology;
    private sealed record PresentationTopology(IReadOnlyList<ZipArchiveEntry> Slides) : OoxmlTopology;
    private sealed record RelationshipTarget(string Target, string Type);

    private static XmlReader CreateReader(Stream stream) => XmlReader.Create(stream, new XmlReaderSettings
    {
        Async = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaximumExpandedXmlBytes,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true
    });

    private async ValueTask<RetainedProcessorCompletion> WriteChildrenAsync(RetainedProcessorClaim claim, string text, CancellationToken cancellationToken)
    {
        var utf8 = new UTF8Encoding(false, true);
        var bytes = utf8.GetBytes(text);
        if (bytes.LongLength > MaximumTextBytes) throw new RetainedProcessorException("office-document-text-limit");
        var children = new List<RetainedProcessorDerivedChild>();
        var offset = 0;
        var ordinal = 0;
        while (offset < bytes.Length)
        {
            if (ordinal == 2) throw new RetainedProcessorException("office-document-text-limit");
            var length = Math.Min((int)MaximumChildTextBytes, bytes.Length - offset);
            while (length > 0 && offset + length < bytes.Length && (bytes[offset + length] & 0xC0) == 0x80) length--;
            if (length == 0) throw new RetainedProcessorException("office-document-text-limit");
            var segment = bytes.AsMemory(offset, length).ToArray();
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"office-segment:{claim.ParentStableIdentity.Length}:{claim.ParentStableIdentity}:{Capability.ProcessorFingerprint}:{ordinal}")));
            var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"office-segment-identity:{claim.ParentStableIdentity.Length}:{claim.ParentStableIdentity}:{fingerprint}")));
            await using var stream = new MemoryStream(segment, writable: false);
            var receipt = await artifactWriter.WriteAsync(claim.SourceRevisionId, stream, MaximumChildTextBytes, cancellationToken).ConfigureAwait(false);
            if (receipt.ByteLength != segment.Length || !receipt.IsUtf8Text || receipt.IsNestedArchive)
                throw new RetainedProcessorException("office-document-part-unsupported");
            children.Add(new RetainedProcessorDerivedChild(fingerprint, $"retained-office-structural-segment:{fingerprint}", identity,
                receipt.ContentSha256, receipt.StoreRelativePath, receipt.ByteLength, "AcceptedUtf8Text", OriginKind: 2, Extension: ".txt"));
            offset += length;
            ordinal++;
        }
        var receiptFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", children.Select(child => $"{child.MemberFingerprint}:{child.ContentSha256}:{child.ByteLength}")))));
        return new RetainedProcessorCompletion(children, receiptFingerprint);
    }
}

/// <summary>Publishes the OOXML descriptor without resolving its scoped retained-artifact writer.</summary>
public sealed class OoxmlStructuralTextCapabilityHandler : ILocalSourceCapabilityHandler
{
    public SourceCapabilityDescriptor Descriptor => OoxmlStructuralTextProcessor.Capability;
}
