using DocAssistant.Api.Modules.Ingestion.Chunking;
using DocAssistant.Api.Modules.Ingestion.Parsing;

namespace DocAssistant.UnitTests.Ingestion.Chunking;

// DocumentChunker.WithoutContextHeader: a chunk's text as the document says it, for a
// reader (docs/decisions.md #63). The header ("Belge: İK Yönetmeliği > Bölüm 2") is for
// search and the model (#27).
public class ContextHeaderTests
{
    [Fact]
    public void RemovesTheHeaderAndTheEmptyLineAfterIt()
    {
        Assert.Equal(
            "Yıllık izin 14 gündür.",
            DocumentChunker.WithoutContextHeader("Belge: İK Yönetmeliği > Bölüm 2: Yıllık İzin\n\nYıllık izin 14 gündür."));
    }

    [Fact]
    public void RemovesAHeaderWithoutASection()
    {
        Assert.Equal("Giriş metni.", DocumentChunker.WithoutContextHeader("Belge: İK Yönetmeliği\n\nGiriş metni."));
    }

    // Only the first empty line ends the header; the paragraphs after it stay apart.
    [Fact]
    public void KeepsTheEmptyLinesBetweenTheParagraphsOfTheText()
    {
        Assert.Equal(
            "Birinci paragraf.\n\nİkinci paragraf.\n\nÜçüncü.",
            DocumentChunker.WithoutContextHeader("Belge: İK Yönetmeliği\n\nBirinci paragraf.\n\nİkinci paragraf.\n\nÜçüncü."));
    }

    // Text written before headers existed, or by something else: nothing to remove.
    [Theory]
    [InlineData("Yıllık izin 14 gündür.")]
    [InlineData("Birinci paragraf.\n\nİkinci paragraf.")]
    [InlineData("belge: küçük harfle başlayan bir cümle.\n\nMetin.")]
    [InlineData("Belge numarası 12.\n\nMetin.")]
    [InlineData("Belge: başlık var ama ardından boş satır yok.")]
    [InlineData("")]
    public void LeavesTextWithoutAHeaderAsItIs(string content)
    {
        Assert.Equal(content, DocumentChunker.WithoutContextHeader(content));
    }

    // Whatever the chunker writes, removing the header gives back exactly the text part.
    [Fact]
    public void GivesBackTheTextOfEveryChunkTheChunkerWrites()
    {
        var chunks = DocumentChunker.Chunk("İK Yönetmeliği", new ParsedDocument([
            new ParagraphBlock("Giriş paragrafı.", 1),
            new HeadingBlock("İK Yönetmeliği", 1, 1),
            new HeadingBlock("Bölüm 1: Çalışma Saatleri", 2, 1),
            new ParagraphBlock("Haftalık çalışma süresi kırk beş saattir.", 1),
            new ParagraphBlock("Mesai 09:00'da başlar.", 1),
            new HeadingBlock("1.1 Fazla Mesai", 3, 2),
            new ParagraphBlock("Fazla mesai yazılı onaya bağlıdır.", 2),
        ]));

        Assert.NotEmpty(chunks);

        foreach (var chunk in chunks)
        {
            var separator = chunk.Content.IndexOf("\n\n", StringComparison.Ordinal);

            Assert.StartsWith("Belge: ", chunk.Content);
            Assert.Equal(chunk.Content[(separator + 2)..], DocumentChunker.WithoutContextHeader(chunk.Content));
            Assert.DoesNotContain("Belge:", DocumentChunker.WithoutContextHeader(chunk.Content));
        }
    }
}
