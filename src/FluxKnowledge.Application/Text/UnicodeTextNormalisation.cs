using System.Buffers;
using System.Text;

namespace FluxKnowledge.Application.Text;

public static class UnicodeTextNormalisation
{
    public static string FormKC(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try { return text.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException)
        {
            // Windows normalisation rejects some valid Unicode noncharacters.
            // They have no decomposition; preserve them and normalise each side.
            var result = new StringBuilder(text.Length);
            var start = 0;
            for (var offset = 0; offset < text.Length;)
            {
                if (Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out var consumed) != OperationStatus.Done) throw;
                if (rune.Value is >= 0xfdd0 and <= 0xfdef || (rune.Value & 0xffff) is 0xfffe or 0xffff)
                {
                    result.Append(text[start..offset].Normalize(NormalizationForm.FormKC));
                    result.Append(text.AsSpan(offset, consumed));
                    start = offset + consumed;
                }
                offset += consumed;
            }
            if (start == 0) throw;
            result.Append(text[start..].Normalize(NormalizationForm.FormKC));
            return result.ToString();
        }
    }
}
