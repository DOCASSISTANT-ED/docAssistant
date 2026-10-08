# Değerlendirme Seti

Arama ve cevap kalitesini ölçmek için kullanılan, cevabı önceden bilinen sorular (`docs/decisions.md` #27, #40). Faz 3'ten itibaren her arama ya da chunking değişikliğinde çalıştırılır.

Sorular `tests/TestData` altındaki örnek belgelerden yazılır; belgelerin yapısı [tests/TestData/README.md](../tests/TestData/README.md) içinde anlatılır.

## `questions.json`

| Alan | Anlamı |
|---|---|
| `id` | Sorunun değişmeyen kimliği: belge kısaltması ve sıra numarası (`ik-007`). Sonuçlar bu kimlikle karşılaştırılır; silinen sorunun numarası yeniden kullanılmaz. |
| `type` | Sorunun türü (aşağıda). Sonuçlara türe göre ayrı bakılır. |
| `question` | Kullanıcının soracağı biçimde soru. |
| `expected_answer` | Doğru cevap, kısa ve belgeye dayalı. Cevabı olmayan sorularda `null`. |
| `sources` | Cevabın bulunduğu yerler. Cevabı olmayan sorularda boş liste. |
| `sources[].document` | Belgenin uzantısız dosya adı (`ik-yonetmeligi`); PDF ve DOCX hâlleri için ortaktır. |
| `sources[].section` | Chunk'ın bölüm yolu, `chunks.section_path` ile aynı yazılır (`Bölüm 1: Çalışma Saatleri > 1.1 Fazla Mesai`). Başlıktan önceki giriş metni için `null`. |
| `sources[].page` | PDF'teki sayfa numarası. DOCX'te sayfa bilgisi yoktur (#22); DOCX sonuçları yalnızca bölümle eşleştirilir. |

## Soru türleri

| Tür | Ne sınar |
|---|---|
| `direct` | Belgedeki sözcüklerle sorulan soru. |
| `paraphrase` | Aynı bilgi başka sözcüklerle sorulur; anlam aramasını (vektör) sınar. |
| `exact_term` | Sayı, kısaltma ya da özel terim içerir (`1.500 TL`, `Md.`); anahtar kelime aramasını sınar. |
| `table` | Cevabı bir tablo satırındadır. |
| `multi_section` | Cevabı birden fazla bölümdedir; kaynakların hepsi bulunmalıdır. |
| `unanswerable` | Belgelerde cevabı yoktur; beklenen davranış "belgelerde bulunamadı" demektir (Faz 4). |

## Soru eklerken

- Cevap tek bir belgeden doğrulanabilmeli; tahmin ya da genel bilgi gerektirmemeli.
- Belgenin cümlesini kopyalayıp soru yapma; kullanıcının soracağı gibi yaz.
- Sınırda kalan değerlerden kaçın (ör. tabloda hem "1–5 yıl" hem "5–15 yıl" satırına uyan "5 yıl").
- Belge değişirse o belgenin sorularını ve kaynaklarını gözden geçir.
