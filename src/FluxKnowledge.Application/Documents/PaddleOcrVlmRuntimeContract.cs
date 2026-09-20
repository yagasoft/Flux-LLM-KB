using System.Security.Cryptography;
using System.Text;

namespace FluxKnowledge.Application.Documents;

/// <summary>
/// The one approved local document-OCR execution profile.  These identifiers are durable GPU
/// fences, not configurable provider selection.
/// </summary>
public static class PaddleOcrVlmRuntimeContract
{
    public const string ExecutorKey = "paddleocr-vl-local-gpu-0";
    public const string CapacitySlotKey = "paddleocr-vl-local-gpu-0";
    public const string CapacityOwnerKey = "paddleocr-vl-local-owner";
    public const string ModelRuntimeKey = "paddleocr-vl-1.6-c5630abae1d940eafe0697512a0325494b02ab42";
    public const string SettingsFingerprint = "3cee8d65e8ec0d8e8818ae71427ae7d6926105affdcd7bfd17847952b7d17007";
    public const long EstimatedDocumentBytes = 4L * 1024 * 1024 * 1024;
    public const int OrientationConfidencePercent = 80;

    public static string DescribeSettings() =>
        "paddleocr-vl-1.6|c5630abae1d940eafe0697512a0325494b02ab42|pp-doclayoutv3|" +
        "7b48a7566925fa464281f930c58eee04fe2c862a|page-orientation|" +
        "7330ab7039123e46af2dc03154b9969aa412c61d|orientation-threshold=0.80|batch=1|" +
        "doc-preprocessor=false|device=gpu:0|use-hpip=false|reading-order=two-column-v1|table=html-tsv-v1";

    public static void AssertFrozenSettings()
    {
        var observed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(DescribeSettings())));
        if (!string.Equals(observed, SettingsFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("paddleocr-vl-settings-fingerprint-invalid");
        }
    }
}
