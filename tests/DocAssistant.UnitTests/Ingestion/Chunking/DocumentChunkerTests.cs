using DocAssistant.Api.Modules.Ingestion.Chunking;
using DocAssistant.Api.Modules.Ingestion.Parsing;

namespace DocAssistant.UnitTests.Ingestion.Chunking;

// Blocks in, chunks out (docs/decisions.md #27, #43). The chunker never sees a file, so
// the tests build the blocks themselves.
public class DocumentChunkerTests
{
    private const string Title = "İK Yönetmeliği";
    private const int Max = DocumentChunker.MaxTextLength;

    // ---- Context header and section path ----
    // Most tests below open like a real document: a level-1 heading with the document's
    // name, then its sections from level 2 down. A document's only level-1 heading names
    // the document instead of opening a section (decisions #46).

    [Fact]
    public void ChunkWithoutHeadingStartsWithTheDocumentTitle()
    {
        var chunk = Assert.Single(Chunk(Paragraph("Metin.")));

        Assert.Equal("Belge: İK Yönetmeliği\n\nMetin.", chunk.Content);
        Assert.Null(chunk.SectionPath);
    }

    [Fact]
    public void ChunkUnderAHeadingCarriesTheHeadingInItsHeader()
    {
        var chunk = Assert.Single(Chunk(
            Heading(Title, 1),
            Heading("Bölüm 1: Çalışma Saatleri", 2),
            Paragraph("Metin.")));

        Assert.Equal("Belge: İK Yönetmeliği > Bölüm 1: Çalışma Saatleri\n\nMetin.", chunk.Content);
        Assert.Equal("Bölüm 1: Çalışma Saatleri", chunk.SectionPath);
    }

    [Fact]
    public void NestedHeadingsFormTheSectionPath()
    {
        var chunk = Assert.Single(Chunk(
            Heading(Title, 1),
            Heading("Bölüm 1", 2),
            Heading("1.1 Fazla Mesai", 3),
            Heading("1.1.1 Hafta Sonu", 4),
            Paragraph("Metin.")));

        Assert.Equal("Bölüm 1 > 1.1 Fazla Mesai > 1.1.1 Hafta Sonu", chunk.SectionPath);
        Assert.Equal("Belge: İK Yönetmeliği > Bölüm 1 > 1.1 Fazla Mesai > 1.1.1 Hafta Sonu", HeaderOf(chunk));
    }

    [Fact]
    public void HeadingAtTheSameLevelReplacesThePreviousOne()
    {
        var chunks = Chunk(
            Heading(Title, 1),
            Heading("Bölüm 1", 2),
            Heading("1.1", 3),
            Paragraph("Bir."),
            Heading("1.2", 3),
            Paragraph("İki."));

        Assert.Equal(["Bölüm 1 > 1.1", "Bölüm 1 > 1.2"], chunks.Select(c => c.SectionPath));
    }

    [Fact]
    public void HigherHeadingClosesTheDeeperOnes()
    {
        var chunks = Chunk(
            Heading("Bölüm 1", 1),
            Heading("1.1", 2),
            Heading("1.1.1", 3),
            Paragraph("Bir."),
            Heading("Bölüm 2", 1),
            Paragraph("İki."));

        Assert.Equal(["Bölüm 1 > 1.1 > 1.1.1", "Bölüm 2"], chunks.Select(c => c.SectionPath));
    }

    [Fact]
    public void HeadingThatSkipsALevelIsStillClosedByAHigherOne()
    {
        var chunks = Chunk(
            Heading(Title, 1),
            Heading("Bölüm 1", 2),
            Heading("Ayrıntı", 4),
            Paragraph("Bir."),
            Heading("1.1", 3),
            Paragraph("İki."));

        Assert.Equal(["Bölüm 1 > Ayrıntı", "Bölüm 1 > 1.1"], chunks.Select(c => c.SectionPath));
    }

    // The title is already in "Belge: <title>"; repeating it as a section adds nothing.
    [Theory]
    [InlineData("İK Yönetmeliği")]
    [InlineData("  İK Yönetmeliği  ")]
    [InlineData("İK YÖNETMELİĞİ")]
    [InlineData("ik yönetmeliği")]
    public void HeadingThatRepeatsTheDocumentTitleIsLeftOutOfThePath(string heading)
    {
        var chunks = Chunk(
            Heading(heading, 1),
            Paragraph("Giriş."),
            Heading("Bölüm 1", 2),
            Paragraph("Metin."));

        Assert.Equal([null, "Bölüm 1"], chunks.Select(c => c.SectionPath));
        Assert.Equal("Belge: İK Yönetmeliği", HeaderOf(chunks[0]));
    }

    [Fact]
    public void HeadingTextIsTrimmed()
    {
        var chunk = Assert.Single(Chunk(Heading(Title, 1), Heading("  Bölüm 1  ", 2), Paragraph("Metin.")));

        Assert.Equal("Bölüm 1", chunk.SectionPath);
    }

    // ---- Sections ----

    // decisions #43: "Bir chunk iki bölüme yayılmaz".
    [Fact]
    public void HeadingClosesTheChunkBeingBuilt()
    {
        var chunks = Chunk(
            Paragraph("Giriş."),
            Heading("Bölüm 1", 1),
            Paragraph("Bir."),
            Heading("Bölüm 2", 1),
            Paragraph("İki."));

        Assert.Equal(["Giriş.", "Bir.", "İki."], chunks.Select(TextOf));
        Assert.Equal([null, "Bölüm 1", "Bölüm 2"], chunks.Select(c => c.SectionPath));
    }

    [Fact]
    public void SectionWithoutTextGivesNoChunk()
    {
        var chunk = Assert.Single(Chunk(
            Heading("Bölüm 1", 1),
            Heading("Bölüm 2", 1),
            Paragraph("Metin.")));

        Assert.Equal("Bölüm 2", chunk.SectionPath);
    }

    [Fact]
    public void DocumentWithoutTextGivesNoChunks()
    {
        Assert.Empty(Chunk());
        Assert.Empty(Chunk(Heading("Bölüm 1", 1), Heading("1.1", 2)));
        Assert.Empty(Chunk(Paragraph(""), Paragraph("  \n ")));
        Assert.Empty(Chunk(Table(hasHeaderRow: true, ["Ad", "Not"])));
    }

    // ---- Paragraphs and the size limit ----

    [Fact]
    public void ShortParagraphsOfASectionShareAChunkSeparatedByABlankLine()
    {
        var chunk = Assert.Single(Chunk(
            Paragraph("Birinci paragraf."),
            Paragraph("  İkinci paragraf.  "),
            Paragraph("Üçüncü paragraf.")));

        Assert.Equal("Birinci paragraf.\n\nİkinci paragraf.\n\nÜçüncü paragraf.", TextOf(chunk));
    }

    [Fact]
    public void ParagraphOfExactlyTheLimitFitsInOneChunk()
    {
        var text = new string('a', Max);

        var chunk = Assert.Single(Chunk(Paragraph(text)));

        Assert.Equal(text, TextOf(chunk));
    }

    // The limit is on the text; the context header does not count (decisions #43).
    [Fact]
    public void ContextHeaderDoesNotCountTowardsTheLimit()
    {
        var text = new string('a', Max);

        var chunk = Assert.Single(Chunk(Heading("Uzun Bir Bölüm Başlığı", 1), Paragraph(text)));

        Assert.Equal(text, TextOf(chunk));
        Assert.True(chunk.Content.Length > Max);
    }

    // 749 + blank line (2) + 749 = 1500.
    [Fact]
    public void ParagraphsThatExactlyFillTheLimitShareAChunk()
    {
        var chunk = Assert.Single(Chunk(
            Paragraph(new string('a', 749)),
            Paragraph(new string('b', 749))));

        Assert.Equal(Max, TextOf(chunk).Length);
    }

    [Fact]
    public void ParagraphThatWouldPassTheLimitOpensANewChunk()
    {
        var first = new string('a', 749);
        var second = new string('b', 750);

        var chunks = Chunk(Paragraph(first), Paragraph(second));

        Assert.Equal([first, second], chunks.Select(TextOf));
    }

    [Fact]
    public void ParagraphOverTheLimitIsSplitBetweenSentences()
    {
        var sentences = Enumerable.Range(1, 30)
            .Select(i => $"Deneme cümlesi numara {i:000} burada yer alır ve yüz karaktere yaklaşması için biraz uzatılmıştır.")
            .ToList();
        var paragraph = string.Join(" ", sentences);

        // As many whole sentences as fit, joined by single spaces.
        var length = sentences[0].Length;
        var perChunk = (Max + 1) / (length + 1);

        var chunks = Chunk(Paragraph(paragraph));

        Assert.True(paragraph.Length > Max);
        Assert.Equal(
            sentences.Chunk(perChunk).Select(group => string.Join(" ", group)),
            chunks.Select(TextOf));
    }

    [Fact]
    public void SentenceOverTheLimitIsSplitBetweenWords()
    {
        // 400 words of 9 characters, no sentence ending anywhere: 150 words and the
        // spaces between them make 1499 characters.
        var words = Enumerable.Range(1, 400).Select(i => $"kelime{i:000}").ToList();

        var chunks = Chunk(Paragraph(string.Join(" ", words)));

        Assert.Equal(
            words.Chunk(150).Select(group => string.Join(" ", group)),
            chunks.Select(TextOf));
    }

    // 1 + 149 × (space + 9) = 1491; one more word would make 1501.
    [Fact]
    public void WordThatWouldPassTheLimitByOneCharacterMovesToTheNextPiece()
    {
        var words = Enumerable.Range(1, 200).Select(i => $"kelime{i:000}").Prepend("a").ToList();

        var chunks = Chunk(Paragraph(string.Join(" ", words)));

        Assert.Equal(
            [string.Join(" ", words.Take(150)), string.Join(" ", words.Skip(150))],
            chunks.Select(TextOf));
        Assert.Equal(1491, TextOf(chunks[0]).Length);
    }

    [Fact]
    public void LongSentenceInsideAParagraphIsSplitWithoutLosingItsNeighbours()
    {
        var longSentence = string.Join(" ", Enumerable.Range(1, 200).Select(i => $"kelime{i:000}")) + ".";
        var paragraph = $"Kısa bir giriş cümlesi. {longSentence} Kısa bir kapanış cümlesi.";

        var chunks = Chunk(Paragraph(paragraph));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.InRange(TextOf(chunk).Length, 1, Max));
        Assert.Equal(paragraph, string.Join(" ", chunks.Select(TextOf)));
    }

    // A long URL or a run of symbols: there is no better place to cut.
    [Fact]
    public void WordOverTheLimitIsCutAtTheLimit()
    {
        var chunks = Chunk(Paragraph(new string('x', (2 * Max) + 200)));

        Assert.Equal([Max, Max, 200], chunks.Select(chunk => TextOf(chunk).Length));
    }

    [Fact]
    public void TextAfterASplitParagraphContinuesInTheLastChunk()
    {
        var words = Enumerable.Range(1, 200).Select(i => $"kelime{i:000}").ToList();

        var chunks = Chunk(Paragraph(string.Join(" ", words)), Paragraph("Sonraki paragraf."));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(string.Join(" ", words.Skip(150)) + "\n\nSonraki paragraf.", TextOf(chunks[1]));
    }

    // decisions #43: no chunk over the limit, no overlap, nothing lost.
    [Fact]
    public void EveryWordOfTheDocumentAppearsOnceAndNoChunkPassesTheLimit()
    {
        var paragraphs = Enumerable.Range(1, 12)
            .Select(p => string.Join(" ", Enumerable.Range(1, 12).Select(s =>
                $"Paragraf {p:00} içindeki {s:00} numaralı cümle, sınırı zorlamak için bilerek uzun yazılmıştır.")))
            .ToList();

        var blocks = new List<DocumentBlock>();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            if (i % 4 == 0)
            {
                blocks.Add(Heading($"Bölüm {(i / 4) + 1}", 1));
            }

            blocks.Add(Paragraph(paragraphs[i]));
        }

        var chunks = Chunk([.. blocks]);

        Assert.True(chunks.Count > 3);
        Assert.All(chunks, chunk => Assert.InRange(TextOf(chunk).Length, 1, Max));
        Assert.Equal(
            paragraphs.SelectMany(WordsOf),
            chunks.SelectMany(chunk => WordsOf(TextOf(chunk))));
    }

    // ---- Tables ----

    [Fact]
    public void TableRowsAreWrittenWithTheirColumnNames()
    {
        var chunk = Assert.Single(Chunk(Table(
            hasHeaderRow: true,
            ["Kıdem", "Yıllık izin günü"],
            ["1–5 yıl", "14"],
            ["5–15 yıl", "20"])));

        Assert.Equal(
            "Kıdem: 1–5 yıl; Yıllık izin günü: 14\nKıdem: 5–15 yıl; Yıllık izin günü: 20",
            TextOf(chunk));
    }

    [Fact]
    public void TableWithoutHeaderRowIsWrittenAsValuesOnly()
    {
        var chunk = Assert.Single(Chunk(Table(
            hasHeaderRow: false,
            ["1–5 yıl", "14"],
            ["5–15 yıl", "20"])));

        Assert.Equal("1–5 yıl; 14\n5–15 yıl; 20", TextOf(chunk));
    }

    [Fact]
    public void EmptyCellsAndEmptyRowsAreLeftOut()
    {
        var chunk = Assert.Single(Chunk(Table(
            hasHeaderRow: true,
            ["Ad", "Not"],
            ["Ali", ""],
            ["", "  "],
            ["  Veli  ", " iyi "])));

        Assert.Equal("Ad: Ali\nAd: Veli; Not: iyi", TextOf(chunk));
    }

    [Fact]
    public void CellWithoutAColumnNameIsWrittenAsValueOnly()
    {
        var chunk = Assert.Single(Chunk(Table(
            hasHeaderRow: true,
            ["Ad", ""],
            ["Ali", "isimsiz sütun", "fazladan hücre"])));

        Assert.Equal("Ad: Ali; isimsiz sütun; fazladan hücre", TextOf(chunk));
    }

    [Fact]
    public void TableIsSeparatedFromParagraphsByABlankLine()
    {
        var chunk = Assert.Single(Chunk(
            Paragraph("Tablodan önce."),
            Table(hasHeaderRow: false, ["a", "b"], ["c", "d"]),
            Paragraph("Tablodan sonra.")));

        Assert.Equal("Tablodan önce.\n\na; b\nc; d\n\nTablodan sonra.", TextOf(chunk));
    }

    // decisions #43: "Tablolar satır sınırından bölünür".
    [Fact]
    public void TableOverTheLimitIsSplitBetweenRows()
    {
        var rows = Enumerable.Range(1, 60)
            .Select(i => new[] { $"K{i:000}", $"Bu satır {i:000} numaralı kaydın açıklamasını taşır" })
            .ToList();
        var expectedLines = rows.Select(row => $"Kod: {row[0]}; Açıklama: {row[1]}").ToList();

        var chunks = Chunk(Table(hasHeaderRow: true, [["Kod", "Açıklama"], .. rows]));

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.InRange(TextOf(chunk).Length, 1, Max));
        Assert.Equal(expectedLines, chunks.SelectMany(chunk => TextOf(chunk).Split('\n')));
    }

    // ---- Page number and order ----

    // decisions #43: "Chunk'ın sayfa numarası ilk bloğunun sayfasıdır".
    [Fact]
    public void PageNumberIsThePageOfTheFirstBlock()
    {
        var chunk = Assert.Single(Chunk(
            Paragraph("Birinci sayfada.", pageNumber: 1),
            Paragraph("İkinci sayfada.", pageNumber: 2)));

        Assert.Equal(1, chunk.PageNumber);
    }

    [Fact]
    public void EachChunkTakesThePageOfTheBlockThatOpensIt()
    {
        var chunks = Chunk(
            Paragraph(new string('a', 749), pageNumber: 1),
            Paragraph(new string('b', 750), pageNumber: 2),
            Heading("Bölüm 2", 1, pageNumber: 2),
            Table(hasHeaderRow: false, pageNumber: 3, ["a", "b"]),
            Paragraph("Tablodan sonra.", pageNumber: 4));

        Assert.Equal([1, 2, 3], chunks.Select(c => c.PageNumber));
    }

    [Fact]
    public void PiecesOfASplitParagraphKeepItsPage()
    {
        var chunks = Chunk(Paragraph(new string('x', Max + 1), pageNumber: 7));

        Assert.Equal([7, 7], chunks.Select(c => c.PageNumber));
    }

    // decisions #22: DOCX has no pages.
    [Fact]
    public void PageNumberIsNullWhenTheBlocksHaveNoPages()
    {
        var chunks = Chunk(Paragraph("Bir."), Heading("Bölüm 1", 1), Paragraph("İki."));

        Assert.All(chunks, chunk => Assert.Null(chunk.PageNumber));
    }

    [Fact]
    public void OrdinalsCountUpFromZero()
    {
        var chunks = Chunk(
            Paragraph("Giriş."),
            Heading("Bölüm 1", 1),
            Paragraph(new string('x', (2 * Max) + 1)),
            Heading("Bölüm 2", 1),
            Paragraph("Son."));

        Assert.Equal([0, 1, 2, 3, 4], chunks.Select(c => c.Ordinal));
    }

    // ---- Helpers ----

    private static IReadOnlyList<ChunkDraft> Chunk(params DocumentBlock[] blocks) =>
        DocumentChunker.Chunk(Title, new ParsedDocument(blocks));

    private static HeadingBlock Heading(string text, int level, int? pageNumber = null) =>
        new(text, level, pageNumber);

    private static ParagraphBlock Paragraph(string text, int? pageNumber = null) => new(text, pageNumber);

    private static TableBlock Table(bool hasHeaderRow, params string[][] rows) => new(rows, hasHeaderRow, null);

    private static TableBlock Table(bool hasHeaderRow, int pageNumber, params string[][] rows) =>
        new(rows, hasHeaderRow, pageNumber);

    // Content is "<header>\n\n<text>".
    private static string HeaderOf(ChunkDraft chunk) => chunk.Content[..HeaderEnd(chunk)];

    private static string TextOf(ChunkDraft chunk) => chunk.Content[(HeaderEnd(chunk) + 2)..];

    private static int HeaderEnd(ChunkDraft chunk)
    {
        var index = chunk.Content.IndexOf("\n\n", StringComparison.Ordinal);
        Assert.True(index > 0, "Chunk content has no blank line after its header.");
        return index;
    }

    private static string[] WordsOf(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
