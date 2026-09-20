using System.Text;
using FluxKnowledge.Application.Sources;
using Syncfusion.Licensing;
using Syncfusion.Telemetry;

namespace FluxKnowledge.Integrations.Documents;

/// <summary>
/// Registers a private Syncfusion runtime key from an explicit local environment
/// path. The key is never logged, persisted, copied, or given a fallback path.
/// </summary>
public sealed class SyncfusionLicenceRegistration(
    Func<string?>? licencePathProvider = null,
    Action<string>? register = null)
{
    public const string LicencePathEnvironmentVariable = "FLUX_KB_SYNCFUSION_LICENCE_FILE";

    private readonly Func<string?> _licencePathProvider = licencePathProvider ??
        (() => Environment.GetEnvironmentVariable(LicencePathEnvironmentVariable));
    private readonly Action<string> _register = register ?? SyncfusionLicenseProvider.RegisterLicense;
    private readonly object _gate = new();
    private int _registered;

    public void EnsureRegistered()
    {
        if (Volatile.Read(ref _registered) != 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_registered != 0)
            {
                return;
            }

            var path = _licencePathProvider();
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new RetainedProcessorException("pdf-license-unavailable");
            }

            string licence;
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new RetainedProcessorException("pdf-license-unavailable");
                }

                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                    FileOptions.SequentialScan);
                using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                    detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);
                licence = reader.ReadToEnd().Trim();
            }
            catch (RetainedProcessorException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
            {
                throw new RetainedProcessorException("pdf-license-unavailable");
            }

            if (string.IsNullOrWhiteSpace(licence))
            {
                throw new RetainedProcessorException("pdf-license-unavailable");
            }

            try
            {
                Telemetry.Disable();
                _register(licence);
                Volatile.Write(ref _registered, 1);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                throw new RetainedProcessorException("pdf-license-unavailable");
            }
        }
    }
}
