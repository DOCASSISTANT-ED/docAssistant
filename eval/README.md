# Değerlendirme Seti

Arama ve cevap kalitesini ölçmek için kullanılan, cevabı önceden bilinen sorular (`docs/decisions.md` #27, #40). Faz 3'ten itibaren her arama ya da chunking değişikliğinde çalıştırılır.

Sorular `tests/TestData` altındaki örnek belgelerden yazılır; belgelerin yapısı [tests/TestData/README.md](../tests/TestData/README.md) içinde anlatılır.

## `questions.json`

| Alan | Anlamı |
|---|---|
| `id` | Sorunun değişmeyen kimliği: belge kısaltması ve sıra numarası (`ik-007`). Birden fazla belgeye dayanan sorular `x-` ile başlar. Sonuçlar bu kimlikle karşılaştırılır; silinen sorunun numarası yeniden kullanılmaz. |
| `type` | Sorunun türü (aşağıda). Sonuçlara türe göre ayrı bakılır. |
| `question` | Kullanıcının soracağı biçimde soru. |
| `expected_answer` | Doğru cevap, kısa ve belgeye dayalı. Cevabı olmayan sorularda `null`. |
| `sources` | Cevabın bulunduğu yerler. Cevabı olmayan sorularda boş liste. |
| `sources[].document` | Belgenin uzantısız dosya adı (`ik-yonetmeligi`); PDF ve DOCX hâlleri için ortaktır. |
| `sources[].section` | Chunk'ın bölüm yolu, `chunks.section_path` ile aynı yazılır (`Bölüm 1: Çalışma Saatleri > 1.1 Fazla Mesai`). Başlıktan önceki giriş metni için `null`. |
| `sources[].page` | Bölümün PDF'te başladığı sayfa, `chunks.page_number` ile aynı (#43). DOCX'te sayfa bilgisi yoktur (#22). |
| `sources[].evidence` | Cevabın geçtiği yer, belgeden birebir alınmış en fazla bir cümle (#47). Kaynak, getirilen chunk'lardan biri bu metni içeriyorsa bulunmuş sayılır. |

`version` alanı dosyanın biçimini gösterir; `evidence` alanı 2. sürümle eklendi.

**Kaynak nasıl eşleştirilir:** Değerlendirme aracı bir kaynağı `evidence` ile eşleştirir; `section` ve `page` sonuçları raporlamak içindir. Kanıt chunk sınırlarından bağımsız olduğu için chunk boyutu ya da örtüşme değişse de aynı soru seti kullanılabilir.

## Soru türleri

| Tür | Ne sınar |
|---|---|
| `direct` | Belgedeki sözcüklerle sorulan soru. |
| `paraphrase` | Aynı bilgi başka sözcüklerle sorulur; anlam aramasını (vektör) sınar. |
| `exact_term` | Sayı, kısaltma ya da özel terim içerir (`1.500 TL`, `Md.`); anahtar kelime aramasını sınar. |
| `table` | Cevabı bir tablo satırındadır. |
| `multi_section` | Cevabı birden fazla bölümdedir; kaynakların hepsi bulunmalıdır. |
| `cross_document` | Cevabı birden fazla belgededir; kaynakların hepsi bulunmalıdır. Belgelerdeki ortak ifadelerin ("yazılı onay", "üç iş günü") aramayı ne kadar şaşırttığını gösterir. |
| `unanswerable` | Belgelerde cevabı yoktur; beklenen davranış "belgelerde bulunamadı" demektir (Faz 4). |

## Belgeler

| Kısaltma | Belge | Soru |
|---|---|---|
| `ik` | `ik-yonetmeligi` | 24 |
| `bg` | `bilgi-guvenligi-politikasi` | 19 |
| `ak` | `arac-kullanim-talimati` | 15 |
| `sa` | `satin-alma-proseduru` | 16 |
| `dg` | `depo-is-guvenligi-talimati` | 16 |
| `uc` | `uzaktan-calisma-politikasi` | 20 |
| `x` | Birden fazla belge | 7 |

Altı belge PDF ve DOCX hâlinde aynı 47 bölüme ayrılır; her bölümün en az bir sorusu vardır. İlk beş belgede her bölüm tek chunk'tır. `uzaktan-calisma-politikasi`'nda bölümler birden çok chunk'a bölünür (DOCX'te 14, PDF'te 15 chunk); chunk boyutunun etkisi bu belgenin sorularıyla ölçülür (#47).

**Silinen sorular:** `ik-024` ("Evden çalışma hangi günlerde yapılabilir?") cevabı olmayan bir soruydu; `uzaktan-calisma-politikasi` eklenince cevaplı hâle geldi ve aynı metinle `uc-018` olarak taşındı.

## Soru eklerken

- Cevap örnek belgelerden doğrulanabilmeli; tahmin ya da genel bilgi gerektirmemeli.
- Belgenin cümlesini kopyalayıp soru yapma; kullanıcının soracağı gibi yaz.
- Sınırda kalan değerlerden kaçın (ör. tabloda hem "1–5 yıl" hem "5–15 yıl" satırına uyan "5 yıl").
- Belge değişirse o belgenin sorularını ve kaynaklarını gözden geçir.
- Her kaynağa bir `evidence` yaz. Kanıt:
  - belgeden harfi harfine kopyalanır ve bir cümleyi aşmaz; cümlenin cevabı taşıyan kısmı da olabilir;
  - hem DOCX'ten hem PDF'ten çıkan chunk'ta aynen geçmelidir. Tablolarda iki biçim farklıdır (DOCX'te `Kıdem: 5–15 yıl; Yıllık izin günü: 20`, PDF'te `5–15 yıl 20`), bu yüzden tablo kanıtı iki biçimde de aynı kalan bir hücre değeridir (`5–15 yıl`). PDF'te iki satıra taşan hücreler komşu hücrelerle karışabilir; kanıtı taşmadan önceki kısımla sınırla;
  - bütün örnek belgelerde yalnızca bir chunk'ta geçmelidir; belgelerde bilerek ortak ifadeler olduğu için ("üç iş günü") kısa kanıtlar başka chunk'larda da bulunabilir.
