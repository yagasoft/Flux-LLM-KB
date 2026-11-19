using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Sources;

namespace FluxKnowledge.Web.Components.Status;

internal static class FileAttentionIssue
{
    internal static bool IsCurrent(PipelineFileItem file) =>
        file.Category is "Blocked" or "Failed" or "Deferred" or "Unconfirmed";

    internal static IReadOnlyList<PipelineFileReasonGroup> GroupReasons(IEnumerable<PipelineFileSample> files) => files
        .GroupBy(sample => sample.File.Reason, StringComparer.Ordinal)
        .Select(group =>
        {
            // Only a single, exact recorded reason is offered as a rule. Combined
            // outcomes remain useful as filters but do not define broad preferences.
            var exact = group.Any(sample => sample.File.CanIgnore &&
                sample.RecordedReasons.Count == 1 && sample.RecordedReasons[0] == group.Key &&
                IsValidRuleReason(group.Key));
            return new PipelineFileReasonGroup(group.Key, Label(group.Key), group.Count(), exact ? group.Key : null);
        })
        .OrderByDescending(group => group.Count).ThenBy(group => group.Label, StringComparer.Ordinal)
        .ThenBy(group => group.Reason, StringComparer.Ordinal).ToArray();

    internal static bool IsValidRuleReason(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) && reason.Length <= 500 &&
        reason == reason.Trim() && !reason.Any(char.IsControl) && !NeedsEvidence(reason) &&
        reason is not ("pipeline-failed" or "processor-branch-blocked" or "No current reason recorded." or "No reason recorded.");

    internal static bool NeedsEvidence(string reason) => reason is
        "source-revision-binding-unconfirmed" or "repository-gpu-outcome-cleanup-or-binding-unconfirmed" or
        "processor-fence-invalid" or "lease-expired-reconciled" or "source-activity-superseded" or
        "source-activity-cancelled" or "document-ocr-source-withdrawn";

    internal static bool IsBinary(string classification, IReadOnlyList<string> reasons) =>
        classification is "PdfDocumentContainer" or "ImageDocumentContainer" or "OoxmlDocumentContainer" or
            "VsdxDocumentContainer" or "ArchiveZip" or "ArchiveTar" or "MediaMetadata" or "UnsupportedBinary" ||
        reasons.Any(reason => reason is
            "binary-format-parser-unavailable" or "compound-binary-parser-unavailable" or
            "legacy-office-binary-parser-unavailable" or "database-sidecar-parser-unavailable" or
            "media-metadata-format-unsupported" or "No installed parser can process this retained binary format." or
            "File bytes contain binary control values and are not accepted as text." or
            "File signature conflicts with its text extension." or "archive-signature-invalid" or
            "archive-zip-expansion-pending" or "archive-tar-expansion-pending" or
            "media-metadata-extraction-pending" or "media-metadata-input-too-large");

    internal static string Label(string reason) => reason switch
    {
        "source-revision-binding-unconfirmed" => "File revision needs verification",
        "pdf-document-container-invalid" => "PDF could not be read",
        "image-document-container-invalid" => "Image could not be decoded",
        "office-document-container-invalid" => "Office package could not be validated",
        "office-document-xml-invalid" => "Office package contains invalid XML",
        "archive-signature-invalid" => "Archive signature could not be validated",
        "archive-entry-path-invalid" => "Archive member path was refused",
        "binary-format-parser-unavailable" or "compound-binary-parser-unavailable" or
            "legacy-office-binary-parser-unavailable" or "database-sidecar-parser-unavailable" or
            "No installed parser can process this retained binary format." => "Binary format has no supported parser",
        "media-metadata-format-unsupported" => "Media format is unsupported",
        "File bytes contain binary control values and are not accepted as text." => "File contains binary data",
        RetainedDefaultPolicy.UnadmittedLegacyTextReason or "File extension is unknown to local source ingestion." =>
            "File needs reassessment under the current source policy",
        "SourceContentNotAvailableLocally" or "SourceAccessContentUnavailable" or
            "source-content-not-available-locally" => "File is unavailable on this device",
        "SourceInUse" => "File is in use",
        "SourceAccessDenied" or "SourceAccessRepairNotApproved" or "SourceAccessExplicitDeny" => "File access needs review",
        "pdf-document-password-required" or "office-document-encrypted" => "File needs to be unlocked",
        "index-storage-capacity-deferred" => "Indexing is waiting for free space",
        _ when reason.Contains('-') && reason.All(character => character is '-' or >= 'a' and <= 'z' or >= '0' and <= '9') =>
            char.ToUpperInvariant(reason[0]) + reason[1..].Replace('-', ' '),
        _ => reason
    };
}
