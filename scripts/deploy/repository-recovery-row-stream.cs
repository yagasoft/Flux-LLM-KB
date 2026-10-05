using System;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace FluxKnowledge.Deployment
{
    public sealed class RetainedRowFingerprint
    {
        public long RowCount { get; set; }
        public string Fingerprint { get; set; }
    }

    public static class RetainedRowStream
    {
        public static RetainedRowFingerprint Read(DbDataReader reader)
        {
            // V1 hashes ordered SQL JSON objects directly. Sequential text reads
            // avoid materialising either a table or a potentially large value.
            var chars = new char[8192];
            var bytes = new byte[Encoding.UTF8.GetMaxByteCount(chars.Length)];
            var encoder = Encoding.UTF8.GetEncoder();
            long count = 0;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                while (reader.Read())
                {
                    if (reader.IsDBNull(0)) throw new InvalidOperationException("Retained row JSON is null.");
                    encoder.Reset();
                    using (var text = reader.GetTextReader(0))
                    {
                        int length;
                        while ((length = text.Read(chars, 0, chars.Length)) != 0)
                        {
                            var encoded = encoder.GetBytes(chars, 0, length, bytes, 0, false);
                            hash.AppendData(bytes, 0, encoded);
                        }
                    }
                    var final = encoder.GetBytes(chars, 0, 0, bytes, 0, true);
                    hash.AppendData(bytes, 0, final);
                    count = checked(count + 1);
                }
                return new RetainedRowFingerprint
                {
                    RowCount = count,
                    Fingerprint = Convert.ToHexString(hash.GetHashAndReset())
                };
            }
        }
    }
}
