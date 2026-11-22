using Xunit;

namespace FluxKnowledge.OutlookHost.Tests;

public sealed class OutlookSignatureImageTests
{
    private const string SignatureImage = """<div id="Signature"><p style="font-family:Calibri">Thanks,<br /><span><img src="cid:image001.png@01DB" width="500" /></span></p></div>""";
    private const string Screenshot = """<p><img src="cid:image002.png@01DB" /></p>""";

    [Fact]
    public void Marks_only_the_hidden_image_exclusive_to_the_outlook_signature()
    {
        var images = OutlookSignatureImages.Find(
            $"""<html xmlns:o="urn:schemas-microsoft-com:office:office"><head><meta charset="utf-8" /></head><body>{Screenshot}{SignatureImage}</body></html>""");
        Assert.Equal(["image001.png@01DB"], images);
    }

    [Theory]
    [InlineData("""<p><img src="cid:image001.png@01DB" /></p>""")]
    [InlineData("""<p><a href="cid:image001.png@01DB">Image</a></p>""")]
    [InlineData("""<!--[if mso]><v:imagedata src="cid:image001.png@01DB" /><![endif]-->""")]
    [InlineData("""<img srcset="cid:image001.png@01DB 2x" />""")]
    [InlineData("""<div id="Signature"></div>""")]
    [InlineData("""<p><img src="cid:image001%2Epng@01DB" /></p>""")]
    [InlineData("""<p><img src="c&#105;d:image001.png@01DB" /></p>""")]
    [InlineData("""<p><img src="ci&#x9;d:image001.png@01DB" /></p>""")]
    [InlineData("""<p><img src="c&#xD;id:image001.png@01DB" /></p>""")]
    [InlineData("""<b>unclosed""")]
    public void Leaves_the_model_ambiguous_when_the_signature_image_is_also_referenced_elsewhere(string additionalHtml)
    {
        Assert.Empty(OutlookSignatureImages.Find($"<html><body>{SignatureImage}{additionalHtml}</body></html>"));
    }

    [Fact]
    public void Leaves_logo_like_images_without_an_explicit_signature_container()
    {
        Assert.Empty(OutlookSignatureImages.Find($"<html><body>{Screenshot}</body></html>"));
    }
}
