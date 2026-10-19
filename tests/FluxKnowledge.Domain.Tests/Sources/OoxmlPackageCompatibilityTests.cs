using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class OoxmlPackageCompatibilityTests
{
    private const string TypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string PresentationNamespace = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string WorkbookType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    private const string WorksheetType = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";

    [Fact]
    public async Task Default_workbook_content_type_resolves_without_a_redundant_override()
    {
        var bytes = Workbook($"<Default Extension='xml' ContentType='{WorkbookType}'/>");
        Assert.Contains("Workbook text", await ExtractAsync(bytes));
    }

    [Fact]
    public async Task Inline_workbook_strings_are_not_silently_omitted()
    {
        Assert.Contains("Workbook text", await ExtractAsync(Workbook(StandardTypes(), inlineString: true)));
    }

    [Fact]
    public async Task Presentation_text_uses_DrawingML_runs_and_does_not_publish_a_PresentationML_decoy()
    {
        var text = await ExtractAsync(Presentation(
            "<a:p><a:r><a:t>Readable slide</a:t></a:r><a:r><a:t> second run</a:t></a:r></a:p><p:t>wrong namespace decoy</p:t>"));

        Assert.Contains("Readable slide", text);
        Assert.Contains(" second run", text);
        Assert.DoesNotContain("wrong namespace decoy", text);
    }

    [Fact]
    public async Task A_presentation_with_only_an_unrecognised_text_namespace_does_not_publish_a_text_child()
    {
        var bytes = Presentation("<p:t>wrong namespace decoy</p:t>");
        var (processor, writer, claim, retained) = Setup(bytes);

        var result = await processor.ProcessAsync(claim, retained, new RetainedProcessorOptions(), CancellationToken.None);

        Assert.Empty(result.Members);
        Assert.Null(writer.Text);
    }

    [Fact]
    public async Task Rich_inline_runs_form_one_cell_value_without_phonetic_annotations()
    {
        var inline = "<is><r><t xml:space='preserve'>First </t></r><r><t>second &amp; third</t></r><rPh sb='0' eb='5'><t>phonetic annotation</t></rPh></is>";
        var text = await ExtractAsync(Workbook(StandardTypes(), inlineString: true, inlineXml: inline));
        Assert.Contains("First second & third", text);
        Assert.DoesNotContain("phonetic annotation", text);
    }

    [Fact]
    public async Task A_separate_preserved_space_run_stays_between_inline_words()
    {
        var inline = "<is><r><t>First</t></r><r><t xml:space='preserve'> </t></r><r><t>second</t></r></is>";
        Assert.Contains("First second", await ExtractAsync(Workbook(StandardTypes(), inlineString: true, inlineXml: inline)));
    }

    [Fact]
    public async Task Blank_inline_cells_remain_valid_without_publishing_an_empty_child()
    {
        var bytes = Workbook(StandardTypes(), inlineString: true, inlineXml: "<is><t/></is>");
        var (processor, writer, claim, retained) = Setup(bytes);
        var result = await processor.ProcessAsync(claim, retained, new RetainedProcessorOptions(), CancellationToken.None);
        Assert.Empty(result.Members);
        Assert.Null(writer.Text);
    }

    [Fact]
    public async Task An_inline_cell_cannot_substitute_a_cached_value_for_its_text()
    {
        await AssertRefusedAsync(Workbook(StandardTypes(), inlineString: true, inlineXml: "<v>unexpected</v>"));
    }

    [Fact]
    public async Task An_inline_cell_cannot_repeat_its_string_value()
    {
        await AssertRefusedAsync(Workbook(StandardTypes(), inlineString: true,
            inlineXml: "<is><t>First</t></is><is><t>Second</t></is>"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_override_wins_over_the_extension_default_in_either_XML_order(bool overrideFirst)
    {
        var fallback = "<Default Extension='xml' ContentType='application/xml'/>";
        var specific = $"<Override PartName='/xl/workbook.xml' ContentType='{WorkbookType}'/>";
        Assert.Contains("Workbook text", await ExtractAsync(Workbook(overrideFirst ? specific + fallback : fallback + specific)));
    }

    [Theory]
    [InlineData("xl/workbook.xml", "worksheets/sheet1.xml")]
    [InlineData("/xl/workbook.xml", "worksheets/sheet1.xml")]
    [InlineData("xl/workbook.xml", "/xl/worksheets/sheet1.xml")]
    [InlineData("/xl/workbook.xml", "/xl/worksheets/sheet1.xml")]
    public async Task Internal_relationships_resolve_relative_or_from_the_package_root(string rootTarget, string sheetTarget)
    {
        Assert.Contains("Workbook text", await ExtractAsync(Workbook(StandardTypes(), rootTarget, sheetTarget)));
    }

    [Theory]
    [InlineData("../xl/workbook.xml", "worksheets/sheet1.xml")]
    [InlineData("xl/workbook.xml", "../../outside.xml")]
    [InlineData("xl/workbook.xml", "//host/xl/worksheets/sheet1.xml")]
    [InlineData("xl/workbook.xml", "https://host/sheet.xml")]
    [InlineData("xl/workbook.xml", "file:///C:/sheet.xml")]
    [InlineData("xl/workbook.xml", "C:/sheet.xml")]
    [InlineData("xl/workbook.xml", "C:\\sheet.xml")]
    [InlineData("xl/workbook.xml", "worksheets\\sheet1.xml")]
    [InlineData("xl/workbook.xml", "/../xl/worksheets/sheet1.xml")]
    public async Task Invalid_internal_targets_never_publish_content(string rootTarget, string sheetTarget)
    {
        await AssertRefusedAsync(Workbook(StandardTypes(), rootTarget, sheetTarget));
    }

    [Fact]
    public async Task Empty_directory_records_do_not_hide_the_document_parts()
    {
        var bytes = Workbook(StandardTypes(), directories: ["xl/", "xl/worksheets/", "_rels/"]);
        Assert.Contains("Workbook text", await ExtractAsync(bytes));
    }

    [Theory]
    [InlineData("../outside/")]
    [InlineData("/outside/")]
    [InlineData("//host/share/")]
    [InlineData("C:/outside/")]
    [InlineData("xl\\outside/")]
    [InlineData("XL/WORKBOOK.XML/")]
    public async Task Directory_records_preserve_path_and_case_insensitive_collision_guards(string directory)
    {
        await AssertRefusedAsync(Workbook(StandardTypes(), directories: [directory]));
    }

    [Fact]
    public async Task A_directory_marker_with_content_is_not_ignored()
    {
        await AssertRefusedAsync(Workbook(StandardTypes(), directories: ["xl/"], directoryPayload: "unexpected"));
    }

    [Fact]
    public async Task Directory_records_still_count_towards_the_package_entry_budget()
    {
        await AssertRefusedAsync(Workbook(StandardTypes(), directories: Enumerable.Range(0, 2_049).Select(index => $"folder{index}/").ToArray()));
    }

    [Theory]
    [InlineData("<Default Extension='xml' ContentType='application/xml'/>")]
    [InlineData("<Default Extension='xml' ContentType='application/xml'/><Default Extension='xml' ContentType='application/xml'/>")]
    [InlineData("<Default Extension='XML' ContentType='application/xml'/><Default Extension='xml' ContentType='application/xml'/>")]
    [InlineData("<Default Extension='../xml' ContentType='application/xml'/>")]
    [InlineData("<Default Extension='xml' ContentType=''/>")]
    public async Task Missing_or_invalid_content_type_mappings_do_not_publish(string types)
    {
        await AssertRefusedAsync(Workbook(types));
    }

    [Fact]
    public async Task A_generic_default_does_not_override_an_invalid_specific_workbook_type()
    {
        var types = $"<Default Extension='xml' ContentType='{WorkbookType}'/><Override PartName='/xl/workbook.xml' ContentType='application/xml'/>";
        await AssertRefusedAsync(Workbook(types));
    }

    [Fact]
    public async Task Multiple_main_document_families_remain_ambiguous()
    {
        var types = StandardTypes() + "<Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/>";
        await AssertRefusedAsync(Workbook(types, extraWordPart: true));
    }

    private static string StandardTypes() => $"<Override PartName='/xl/workbook.xml' ContentType='{WorkbookType}'/>";

    private static byte[] Presentation(string body)
    {
        using var output = new MemoryStream();
        using (var package = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(package, "[Content_Types].xml", $"<Types xmlns='{TypesNamespace}'><Override PartName='/ppt/presentation.xml' ContentType='application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml'/><Override PartName='/ppt/slides/slide1.xml' ContentType='application/vnd.openxmlformats-officedocument.presentationml.slide+xml'/></Types>");
            Write(package, "_rels/.rels", $"<Relationships xmlns='{RelationshipsNamespace}'><Relationship Id='main' Type='{OfficeRelationships}/officeDocument' Target='ppt/presentation.xml'/></Relationships>");
            Write(package, "ppt/presentation.xml", $"<p:presentation xmlns:p='{PresentationNamespace}' xmlns:r='{OfficeRelationships}'><p:sldIdLst><p:sldId id='1' r:id='slide1'/></p:sldIdLst></p:presentation>");
            Write(package, "ppt/_rels/presentation.xml.rels", $"<Relationships xmlns='{RelationshipsNamespace}'><Relationship Id='slide1' Type='{OfficeRelationships}/slide' Target='slides/slide1.xml'/></Relationships>");
            Write(package, "ppt/slides/slide1.xml", $"<p:sld xmlns:p='{PresentationNamespace}' xmlns:a='{DrawingNamespace}'><p:txBody>{body}</p:txBody></p:sld>");
        }
        return output.ToArray();
    }

    private static byte[] Workbook(string types, string rootTarget = "xl/workbook.xml", string sheetTarget = "worksheets/sheet1.xml",
        string[]? directories = null, string directoryPayload = "", bool extraWordPart = false, bool inlineString = false, string? inlineXml = null)
    {
        using var output = new MemoryStream();
        using (var package = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var directory in directories ?? []) Write(package, directory, directoryPayload);
            Write(package, "[Content_Types].xml", $"<Types xmlns='{TypesNamespace}'>{types}<Override PartName='/xl/worksheets/sheet1.xml' ContentType='{WorksheetType}'/></Types>");
            Write(package, "_rels/.rels", $"<Relationships xmlns='{RelationshipsNamespace}'><Relationship Id='main' Type='{OfficeRelationships}/officeDocument' Target='{rootTarget}'/></Relationships>");
            Write(package, "xl/workbook.xml", $"<workbook xmlns='{SpreadsheetNamespace}' xmlns:r='{OfficeRelationships}'><sheets><sheet name='Sheet' sheetId='1' r:id='sheet1'/></sheets></workbook>");
            Write(package, "xl/_rels/workbook.xml.rels", $"<Relationships xmlns='{RelationshipsNamespace}'><Relationship Id='sheet1' Type='{OfficeRelationships}/worksheet' Target='{sheetTarget}'/></Relationships>");
            var cell = inlineString ? "<c r='A1' t='inlineStr'>" + (inlineXml ?? "<is><t>Workbook text</t></is>") + "</c>" : "<c r='A1' t='str'><v>Workbook text</v></c>";
            Write(package, "xl/worksheets/sheet1.xml", $"<worksheet xmlns='{SpreadsheetNamespace}'><sheetData><row r='1'>{cell}</row></sheetData></worksheet>");
            if (extraWordPart) Write(package, "word/document.xml", "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'/>");
        }
        return output.ToArray();
    }

    private static async Task<string> ExtractAsync(byte[] bytes)
    {
        var (processor, writer, claim, retained) = Setup(bytes);
        var result = await processor.ProcessAsync(claim, retained, new RetainedProcessorOptions(), CancellationToken.None);
        Assert.Single(result.Members);
        return Assert.IsType<string>(writer.Text);
    }

    private static async Task AssertRefusedAsync(byte[] bytes)
    {
        var (processor, writer, claim, retained) = Setup(bytes);
        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() =>
            processor.ProcessAsync(claim, retained, new RetainedProcessorOptions(), CancellationToken.None).AsTask());
        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Null(writer.Text);
    }

    private static (OoxmlStructuralTextProcessor Processor, RecordingWriter Writer, RetainedProcessorClaim Claim, RetainedSourceBytes Retained) Setup(byte[] bytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "package-compatibility-fixture", hash,
            "test-owner", 1, DateTimeOffset.UtcNow.AddMinutes(1));
        var writer = new RecordingWriter();
        return (new OoxmlStructuralTextProcessor(writer), writer, claim, new(claim.SourceRevisionId, bytes, hash, bytes.Length));
    }

    private static void Write(ZipArchive package, string path, string content)
    {
        using var writer = new StreamWriter(package.CreateEntry(path, CompressionLevel.NoCompression).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private sealed class RecordingWriter : IRetainedArtifactWriter
    {
        public string? Text { get; private set; }
        public async ValueTask<RetainedArtifactWriteReceipt> WriteAsync(SourceRevisionId sourceRevisionId,
            Stream stream, long maximumByteLength, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            Text = await reader.ReadToEndAsync(cancellationToken);
            var bytes = Encoding.UTF8.GetBytes(Text);
            Assert.True(bytes.LongLength <= maximumByteLength);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return new(hash, "fixture/" + hash, bytes.Length, true, false);
        }
    }
}
