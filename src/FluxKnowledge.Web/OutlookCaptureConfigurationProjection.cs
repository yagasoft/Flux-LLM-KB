using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace FluxKnowledge.Web;

/// <summary>
/// The complete deployment-safe Outlook configuration projection. It deliberately contains no
/// identifiers, paths, content, credentials or diagnostic values.
/// </summary>
public sealed record OutlookCaptureConfigurationProjection(
    [property: JsonPropertyName("outlook_enabled")] bool OutlookEnabled)
{
    public static OutlookCaptureConfigurationProjection Create(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = WebHostComposition.ReadOutlookRecoveryOptions(configuration);
        return new OutlookCaptureConfigurationProjection(options.Enabled);
    }
}
