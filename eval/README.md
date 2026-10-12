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

## Değerlendirmeyi çalıştırmak

`eval/DocAssistant.Eval` soruları gerçek arama hattına sorar ve sonucu raporlar. Docker (geçici bir PostgreSQL için) ve `bge-m3` yüklü Ollama açık olmalıdır.

```bash
dotnet run --project eval/DocAssistant.Eval
```

Başka bir Ollama adresi ya da model için: `-- --embedding-url http://localhost:11434 --embedding-model bge-m3`.

Araç bir test projesi değildir; `dotnet test` ve CI onu çalıştırmaz, çünkü CI'da embedding modeli yoktur.

### Ne yapar

1. Geçici bir veritabanı açar ve bütün migration'ları uygular. Geliştirme veritabanına dokunmaz.
2. API'yi bellekte başlatır: `Program.cs`, endpoint'ler, arka plan işi ve arama gerçektir. Yalnızca dosya deposu bellektedir.
3. Her belge türü için ayrı bir firma kaydeder (`POST /auth/register`) ve `tests/TestData` altındaki o türden belgeleri yükler (`POST /documents`); belgeleri uygulamanın kendi arka plan işi işler. PDF ve DOCX ayrı firmalarda durduğu için sonuçlar türe göre ayrıdır (#27).
4. Cevabı olan her soruyu üç yolla sorar: yalnızca anahtar kelime araması, yalnızca vektör araması ve bütün hat (`IChunkRetrieval`, sohbetin çağıracağı giriş noktası). Üçünden de hattın döndürdüğü kadar chunk istenir (`SearchOptions.Results`, 8).
5. Her kaynak için, kanıt metnini içeren ilk chunk'ın kaçıncı sırada geldiğine bakar (#47).

### Rapor nasıl okunur

| Sütun | Anlamı |
|---|---|
| `top 1`, `top 5`, `top 8` | Kanıtı ilk 1, ilk 5 ya da dönen 8 chunk içinde bulunan kaynak sayısı. |
| `MRR` | Ortalama karşılıklı sıra: kaynak 1. sıradaysa 1, 2. sıradaysa 1/2, bulunamadıysa 0; hepsinin ortalaması. Doğru chunk'ın ne kadar yukarıda olduğunu ölçer. |
| Türe göre tablo | Soru türüne göre, dönen chunk'lar içinde bulunan kaynak sayısı. Bir değişikliğin hangi tür soruyu etkilediğini gösterir. |
| `Hybrid is better / worse than vector search alone` | Faz 3'ün bitiş koşulunun karşılaştırması (MRR ile). |
| `Sources hybrid did not find` | Hattın dönen chunk'lar içinde bulamadığı kaynakların soruları. |
| `Evidence in no chunk of this format` | Kanıtı o türün hiçbir chunk'ında geçmeyen kaynaklar. Sorun aramada değil, parser'da ya da soru setindedir. |

Cevabı olmayan sorular (`unanswerable`) ölçülmez: arama her zaman bir şey döndürür; "belgelerde bulunamadı" demek sohbet modelinin işidir (Faz 4).

Her çalıştırma ayrıntılarını `eval/results/<tarih-saat>.json` dosyasına yazar: her kaynağın üç yöntemdeki sırası. İki çalıştırmayı soru soru karşılaştırmak içindir. Klasör Git'e girmez; bir karara dayanak olan sonuçlar ilgili karara ya da PR'a sayı olarak yazılır.

### Sayılar çalıştırmadan çalıştırmaya biraz oynar

Vektör aramasının sonuçları her çalıştırmada aynıdır. Anahtar kelime aramasında ise birçok chunk aynı puanı alır ve eşitlik chunk kimliğine göre bozulur; kimlikler her çalıştırmada yeniden üretildiği için sıra değişir. Bu, anahtar kelime ve hibrit sayılarını birkaç kaynak oynatır. Küçük farkları yorumlamadan önce aracı iki kez çalıştır.

## Soru eklerken

- Cevap örnek belgelerden doğrulanabilmeli; tahmin ya da genel bilgi gerektirmemeli.
- Belgenin cümlesini kopyalayıp soru yapma; kullanıcının soracağı gibi yaz.
- Sınırda kalan değerlerden kaçın (ör. tabloda hem "1–5 yıl" hem "5–15 yıl" satırına uyan "5 yıl").
- Belge değişirse o belgenin sorularını ve kaynaklarını gözden geçir.
- Her kaynağa bir `evidence` yaz. Kanıt:
  - belgeden harfi harfine kopyalanır ve bir cümleyi aşmaz; cümlenin cevabı taşıyan kısmı da olabilir;
  - hem DOCX'ten hem PDF'ten çıkan chunk'ta aynen geçmelidir. Tablolarda iki biçim farklıdır (DOCX'te `Kıdem: 5–15 yıl; Yıllık izin günü: 20`, PDF'te `5–15 yıl 20`), bu yüzden tablo kanıtı iki biçimde de aynı kalan bir hücre değeridir (`5–15 yıl`). PDF'te iki satıra taşan hücreler komşu hücrelerle karışabilir; kanıtı taşmadan önceki kısımla sınırla;
  - bütün örnek belgelerde yalnızca bir chunk'ta geçmelidir; belgelerde bilerek ortak ifadeler olduğu için ("üç iş günü") kısa kanıtlar başka chunk'larda da bulunabilir.
