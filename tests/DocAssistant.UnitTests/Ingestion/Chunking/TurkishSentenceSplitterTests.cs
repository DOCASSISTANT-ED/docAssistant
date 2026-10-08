using DocAssistant.Api.Modules.Ingestion.Chunking;

namespace DocAssistant.UnitTests.Ingestion.Chunking;

// Sentence boundaries for paragraphs that are too long for one chunk (docs/decisions.md #43).
public class TurkishSentenceSplitterTests
{
    [Fact]
    public void EveryTerminatorEndsASentence()
    {
        var sentences = TurkishSentenceSplitter.Split(
            "Bugün hava güzel. Yarın yağmur yağacak! Gelecek misin? Belki… Sonra bakarız.");

        Assert.Equal(
            ["Bugün hava güzel.", "Yarın yağmur yağacak!", "Gelecek misin?", "Belki…", "Sonra bakarız."],
            sentences);
    }

    [Theory]
    [InlineData("Gerçekten mi?! Evet.", "Gerçekten mi?!", "Evet.")]
    [InlineData("Bekle... Geliyorum.", "Bekle...", "Geliyorum.")]
    public void RepeatedTerminatorsStayWithTheirSentence(string text, string first, string second)
    {
        Assert.Equal([first, second], TurkishSentenceSplitter.Split(text));
    }

    [Theory]
    [InlineData("\"Yarın gelirim.\" Sonra çıktı.", "\"Yarın gelirim.\"", "Sonra çıktı.")]
    [InlineData("“Yarın gelirim.” Sonra çıktı.", "“Yarın gelirim.”", "Sonra çıktı.")]
    [InlineData("(Ayrıntılar ektedir.) Sonraki madde aşağıdadır.", "(Ayrıntılar ektedir.)", "Sonraki madde aşağıdadır.")]
    public void ClosingMarksStayWithTheirSentence(string text, string first, string second)
    {
        Assert.Equal([first, second], TurkishSentenceSplitter.Split(text));
    }

    // decisions #43: "Türkçe kısaltmalarda cümle kesilmez".
    [Theory]
    [InlineData("Toplantıya Dr. Ayşe Kaya katıldı.")]
    [InlineData("Konuşmayı Prof. Dr. Mehmet Öz yaptı.")]
    [InlineData("Örnek Ltd. Şti. Ankara'da kuruldu.")]
    [InlineData("Ayrıntı için bkz. Ek 3 ve md. 12 hükümleri.")]
    [InlineData("Şirket Atatürk Cad. No. 5 adresindedir.")]
    public void KnownAbbreviationDoesNotEndASentence(string text)
    {
        Assert.Equal([text], TurkishSentenceSplitter.Split(text));
    }

    // Turkish casing: İ is the capital of i, I is the capital of ı.
    [Theory]
    [InlineData("ÖRNEK LTD. ŞTİ. Ankara'da kuruldu.")]
    [InlineData("Bu işlem FIK. 2 uyarınca yapılır.")]
    [InlineData("Toplantıya DR. Ayşe Kaya katıldı.")]
    public void AbbreviationsAreRecognisedInCapitals(string text)
    {
        Assert.Equal([text], TurkishSentenceSplitter.Split(text));
    }

    [Fact]
    public void InitialDoesNotEndASentence()
    {
        const string text = "Raporu A. Yılmaz hazırladı.";

        Assert.Equal([text], TurkishSentenceSplitter.Split(text));
    }

    [Theory]
    [InlineData("Örnek A.Ş. Yönetim Kurulu toplandı.")]
    [InlineData("Başvuruda T.C. Kimlik numarası istenir.")]
    public void DottedAbbreviationDoesNotEndASentence(string text)
    {
        Assert.Equal([text], TurkishSentenceSplitter.Split(text));
    }

    // Turkish writes ordinals as a number and a period.
    [Theory]
    [InlineData("Bu konu 3. Maddede anlatılır.")]
    [InlineData("Ofis 2. Kat'tadır.")]
    public void OrdinalNumberDoesNotEndASentence(string text)
    {
        Assert.Equal([text], TurkishSentenceSplitter.Split(text));
    }

    [Theory]
    [InlineData("Dosya rapor. pdf adıyla kaydedildi.")]
    [InlineData("Sürüm 2.5 yayımlandı.")]
    [InlineData("Ayrıntılar www.ornek.com adresindedir.")]
    public void PeriodNotFollowedByASentenceStartDoesNotSplit(string text)
    {
        Assert.Equal([text], TurkishSentenceSplitter.Split(text));
    }

    [Theory]
    [InlineData("İzin süresi on gündür. 2024 yılında artırıldı.", "İzin süresi on gündür.", "2024 yılında artırıldı.")]
    [InlineData("Süre on gündür. (Bkz. Ek 2.)", "Süre on gündür.", "(Bkz. Ek 2.)")]
    [InlineData("Şartlar şunlardır. - Kimlik fotokopisi gerekir.", "Şartlar şunlardır.", "- Kimlik fotokopisi gerekir.")]
    public void SentenceCanStartWithADigitAnOpeningMarkOrADash(string text, string first, string second)
    {
        Assert.Equal([first, second], TurkishSentenceSplitter.Split(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void TextWithoutContentGivesNoSentences(string text)
    {
        Assert.Empty(TurkishSentenceSplitter.Split(text));
    }

    [Theory]
    [InlineData("Tek cümle.")]
    [InlineData("Sonunda nokta yok")]
    public void SingleSentenceComesBackAsIs(string text)
    {
        Assert.Equal([text], TurkishSentenceSplitter.Split(text));
    }

    [Fact]
    public void SentencesAreTrimmed()
    {
        var sentences = TurkishSentenceSplitter.Split("  Birinci cümle.   \n İkinci cümle.  ");

        Assert.Equal(["Birinci cümle.", "İkinci cümle."], sentences);
    }

    // The chunker joins the pieces with spaces, so nothing may get lost in between.
    [Theory]
    [InlineData("Bugün hava güzel. Yarın yağmur yağacak! Gelecek misin? Belki… Sonra bakarız.")]
    [InlineData("Örnek A.Ş. 2020'de kuruldu. Merkezi Atatürk Cad. No. 5 adresindedir. (Bkz. Ek 2.) Sonu yok")]
    [InlineData("Madde 3. maddeye göre uygulanır. \"Süre on gündür.\" Dr. Kaya onayladı.")]
    public void JoiningTheSentencesGivesBackTheText(string text)
    {
        var sentences = TurkishSentenceSplitter.Split(text);

        Assert.True(sentences.Count > 1);
        Assert.Equal(text, string.Join(" ", sentences));
    }
}
