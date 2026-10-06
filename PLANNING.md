# DocAssistant — Proje Planı

> KOBİ'ler için çok kiracılı (multi-tenant) Türkçe doküman asistanı.
> Firmalar kendi belgelerini yükler, çalışanlar bu belgelere soru sorar, cevaplar kaynak gösterilerek gelir.

**Ekip:** Dide, Erva
**Hedef:** Canlıya alınmış, pilot firmalarla denenmiş `v0.1.0` sürümü

---

## 1. Ürün Kapsamı

### MVP'de olacaklar
- Firma (tenant) kaydı ve kullanıcı daveti
- Belge yükleme: PDF, DOCX (XLSX Faz 2 sonrası)
- Belgelere kaynaklı soru-cevap (cevaptaki her iddia [1], [2] şeklinde kaynağa bağlı)
- Sohbet geçmişi
- Basit yönetim paneli: yüklü belgeler, işlenme durumu, token kullanımı

### Bilinçli olarak sonraya bırakılanlar
- Taranmış PDF için OCR
- Teams / Slack entegrasyonu
- Google Drive senkronu
- Belge bazlı yetki ("bu klasörü sadece İK görsün")
- Ödeme altyapısı

---

## 2. Kararlar

Bu bölüm başlangıç toplantısında doldurulacak. Değişen her karar tarihiyle birlikte `docs/decisions.md` dosyasına da yazılır.

| Konu | Karar |
|---|---|
| Proje adı / namespace | `DocAssistant` (kesinleşecek) |
| Repo sahibi | Organizasyon hesabı (önerilen) |
| .NET sürümü | .NET 10 |
| Arayüz | Blazor Web App |
| Veritabanı | PostgreSQL 17 + pgvector |
| Dosya saklama | SeaweedFS (geliştirme), S3 uyumlu depolama (canlı). MinIO'dan vazgeçildi, bkz. `docs/decisions.md` #9 |
| Embedding modeli | bge-m3 (Ollama) |
| Sohbet modeli | Geliştirmede Ollama, canlıda API modeli (`IChatClient` ile değiştirilebilir) |
| Branch stratejisi | `main` korumalı, `feature/*` branch'leri, en az 1 onaylı PR |
| Merge yöntemi | Merge commit |
| Commit formatı | Conventional Commits (`feat:`, `fix:`, `test:`, `docs:`, `chore:`) |

### "Bitti" tanımı
Bir iş şu dört koşul sağlanınca tamamlanmış sayılır:
1. Testi yazılmış (çapraz test kuralına göre)
2. CI yeşil
3. Karşı taraf review edip onaylamış
4. Gerekiyorsa README veya dokümantasyon güncellenmiş

---

## 3. Mimari

### Genel yapı
Modüler monolit. Mikroservis yok.

```
src/
  DocAssistant.Api            ASP.NET Core Web API
    Data/                     AppDbContext (tek), migration'lar
    Modules/
      Tenants                 Firma yönetimi, tenant çözümleme
      Identity                Kayıt, giriş, JWT, üyelikler
      Documents               Yükleme, saklama, belge durumu
      Ingestion               Parse → chunk → embed hattı
      Retrieval               Hibrit arama, reranking
      Chat                    LLM, prompt, sohbet geçmişi
  DocAssistant.Web            Blazor Web App
  DocAssistant.Shared         Ortak tipler, sonuç nesneleri, ITenantContext
tests/
  DocAssistant.UnitTests
  DocAssistant.IntegrationTests
docs/
  decisions.md
```

Modüller ayrı proje değil, API projesi içinde klasördür (bkz. `docs/decisions.md` #16).

Kök dizinde `Directory.Build.props`, `Directory.Packages.props` (merkezi paket sürümü) ve `.editorconfig` bulunur.

### Ingestion hattı
1. **Parse:** PDF için PdfPig, DOCX için OpenXML SDK, XLSX için ClosedXML. Metinle birlikte sayfa numarası, başlık hiyerarşisi ve tablolar korunur.
2. **Chunking:** Yapıya göre bölme (bölüm → paragraf → cümle).
   - Türkçe kısaltmalarda ("Dr.", "vb.", "md.") yanlış cümle kesilmez.
   - Her chunk'ın başına bağlam başlığı eklenir: `Belge: İK Yönetmeliği > Bölüm 3: İzinler`
   - Tablolar satır bazında, başlık satırıyla birlikte saklanır.
3. **Embedding:** bge-m3. Hangi model ve sürümle üretildiği her chunk'a yazılır.
4. **Durum takibi:** Belge durumu veritabanında tutulur: `Pending → Processing → Done / Failed`. Uygulama açılışında yarım kalan işler tekrar kuyruğa alınır. (`Channel` bellekte durduğu için yeniden başlatmada iş kaybolmasın diye.)

### Chunk metadata (zorunlu alanlar)
`TenantId`, `DocumentId`, `DocumentVersion`, `PageNumber`, `SectionPath`, `EmbeddingModel`, `CreatedAt`

Belge yeniden yüklendiğinde eski sürümün chunk'ları silinir.

### Retrieval
1. **Hibrit arama:** pgvector benzerlik araması + PostgreSQL `turkish` full-text araması, Reciprocal Rank Fusion (RRF) ile birleştirilir.
2. **Reranking:** İlk ~30 sonuç bir reranker modeliyle en iyi 5–8'e indirilir.
3. **Cevap üretimi:** Chunk'lar LLM'e numaralı verilir. Prompt kuralları: sadece verilen kaynaklara dayan, her iddiaya kaynak numarası koy, bilgi yoksa "belgelerde bulunamadı" de.
4. **Arayüz:** Kaynak numarasına tıklanınca ilgili belgenin ilgili sayfası açılır.

### Tenant izolasyonu (iki katman)
1. **Uygulama:** EF Core global query filter ile her sorguya otomatik `TenantId` filtresi.
2. **Veritabanı:** PostgreSQL Row-Level Security. Kodda filtre unutulsa bile başka firmanın verisi dönmez.

RLS yalnızca firma verisi tablolarında açıktır; kimlik tablolarını (`users`, `memberships`, `tenants`) yalnızca query filter korur. Şu anki firma `ITenantContext` üzerinden okunur. Uygulama veritabanına yetkileri kısıtlı ayrı bir kullanıcıyla bağlanır; süper kullanıcı RLS'ten muaf olduğu için yalnızca migration'larda kullanılır. Ayrıntılar: `docs/decisions.md` #12–15.

Filtreli vektör aramanın HNSW indeksiyle verimli çalışması için pgvector 0.8+ kullanılır.

---

## 4. Çalışma Kuralları

İş bölümü katmanlara göre değil, **her teknolojiye ikimizin de dokunacağı** şekilde yapılır.

- **Paralel ikizler:** Aynı türden iki iş varsa ikiye bölünür (ör. biri PDF parser, diğeri DOCX parser).
- **Çapraz test:** Herkes karşı tarafın yazdığı kodun testini yazar. Bu kural **her fazda** geçerlidir.
- **Pair programming:** Chunking ve prompt tasarımı tek klavyede birlikte yazılır, yarı zamanda sürücü değişir.

---

## 5. Faz Planı

### Faz 0 — Kurulum

| Dide | Erva | Birlikte |
|---|---|---|
| Solution iskeleti, `Directory.*.props`, `.editorconfig` | Repo ayarları: branch protection, PR ve issue şablonları, etiketler, Project panosu | Başlangıç toplantısı, `docs/decisions.md` |
| GitHub Actions: build pipeline | `docker-compose.yml` (pgvector), `.env.example`, Ollama + bge-m3 denemesi | `README.md`: sıfırdan kurulum adımları |
| **Çapraz PR:** Compose'a SeaweedFS (S3) ekler | **Çapraz PR:** CI'a test adımı ekler | |

**Bitiş koşulu:**
- [x] İki bilgisayarda da `git clone` → `docker compose up` → `dotnet run` sorunsuz çalışıyor
- [x] CI yeşil
- [x] Boş bir test geçiyor
- [x] README'de kurulum adımları yazılı

> Windows notu: Ollama'yı geliştirme sırasında doğrudan Windows'a kurmak, Docker içinde GPU ayarlamaktan çok daha kolay. Compose'a sadece Linux sunucu için bir profil olarak eklenebilir.

### Faz 1 — Temeller

| Dide | Erva | Birlikte |
|---|---|---|
| Identity + JWT (kayıt, giriş), `users` ve `memberships` tabloları | EF Core kurulumu, `AppDbContext`, Tenant entity, migration'lar, global query filter | Başlangıç kararları: `docs/decisions.md` #11–19 |
| `ITenantContext`'in JWT'den okuyan uygulaması | `documents` tablosunun yalın hâli, PostgreSQL Row-Level Security politikaları, kısıtlı veritabanı kullanıcısı | |
| Testcontainers ile entegrasyon test altyapısı (gerçek PostgreSQL) | Kimlik tablolarının query filter'ları (`users` ve `memberships` `main`'e girdikten sonra) | |
| **Çapraz test:** Tenant izolasyon testleri (query filter + RLS) | **Çapraz test:** Auth testleri | |

**Bitiş koşulu:**
- [x] Bir kullanıcı kayıt olup giriş yapabiliyor
- [x] İki farklı tenant'ın verisi birbirine görünmüyor (testle kanıtlanmış)

### Faz 2 — Ingestion

| Dide | Erva | Birlikte |
|---|---|---|
| DOCX parser (OpenXML) | PDF parser (PdfPig) | **Pair:** Chunking algoritması |
| Arka plan işi (`BackgroundService` + `Channel`) ve belge durum takibi, açılışta yarım işleri kurtarma | Yükleme endpoint'i, S3'e (SeaweedFS) dosya saklama | **İlk değerlendirme seti:** 30–50 soru-cevap çifti |
| **Çapraz test:** PDF parser testleri | **Çapraz test:** DOCX parser ve arka plan işi testleri | |

**Bitiş koşulu:**
- [ ] Yüklenen PDF ve DOCX chunk'lara bölünüp metadata ile veritabanına yazılıyor
- [ ] Uygulama işlem ortasında kapatılıp açılınca yarım kalan belge tekrar işleniyor

### Faz 3 — Arama

| Dide | Erva | Birlikte |
|---|---|---|
| PostgreSQL `turkish` full-text arama | Embedding hattı (`IEmbeddingGenerator`, bge-m3), pgvector + HNSW | Değerlendirme setini çalıştırıp sonuçları birlikte inceleme |
| Re-indexleme işi (embedding modeli değişince chunk'ları yeniden embed etme) | RRF ile hibrit birleştirme | |
| Reranker entegrasyonu | | |
| **Çapraz test:** Embedding hattı ve RRF testleri | **Çapraz test:** Full-text arama ve reranker testleri | |

**Bitiş koşulu:**
- [ ] Hibrit arama, değerlendirme setinde tek başına vektör aramadan daha iyi sonuç veriyor (ölçülmüş)

### Faz 4 — Sohbet

| Dide | Erva | Birlikte |
|---|---|---|
| Blazor sohbet ekranı, streaming'in arayüz tarafı | `IChatClient` entegrasyonu, streaming endpoint'i | **Pair:** Prompt tasarımı ve kaynak numaralandırma |
| Kaynağa tıklayınca belge sayfasını açma | Sohbet geçmişi | |
| **Çapraz test:** Sohbet backend testleri | **Çapraz test:** Sohbet arayüzü testleri (bUnit) | |

> Daha dengeli olsun istenirse bu fazda backend ve arayüz yer değiştirilebilir.

**Bitiş koşulu:**
- [ ] Soru sorulunca cevap akarak (streaming) geliyor, her iddiada kaynak numarası var
- [ ] Belgede olmayan bir soruya "belgelerde bulunamadı" cevabı veriliyor

### Faz 5 — Yayın

| Dide | Erva | Birlikte |
|---|---|---|
| Entegrasyon testlerini genişletme (Testcontainers altyapısı Faz 1'de kuruldu) | Blazor belge yönetimi sayfası | `v0.1.0` release |
| Docker ile canlı ortama deploy | Değerlendirme setini genişletme (100+ soru) | 2–3 pilot firma ile deneme |
| | Tenant bazlı token kotası ve rate limiting | |
| **Çapraz test:** Belge yönetimi ve kota testleri | **Çapraz test:** Deploy sonrası smoke testleri | |

**Bitiş koşulu:**
- [ ] Uygulama canlı ortamda HTTPS üzerinden erişilebilir
- [ ] Her tenant'ın token kullanımı kayıt altında ve kota aşılınca istek reddediliyor
- [ ] Değerlendirme seti CI'da veya elle tek komutla çalıştırılabiliyor

---

## 6. Denge Kontrolü: Herkes Her Teknolojiye Dokundu mu?

| Teknoloji | Dide | Erva |
|---|---|---|
| ASP.NET Core / Web API | Auth endpoint'leri | Yükleme ve sohbet endpoint'leri |
| EF Core + migration | Auth tabloları | Tenant yapısı, query filter, RLS |
| Docker | SeaweedFS ekleme, canlı deploy | Compose kurulumu |
| CI (GitHub Actions) | İlk pipeline | Test adımı |
| Belge parsing | DOCX | PDF |
| Arka plan işleri | Ingestion işi, durum takibi | Testleri |
| Chunking | Pair | Pair |
| Embedding / vektör arama | Re-indexleme işi | Embedding hattı, HNSW |
| Full-text arama | Yazıyor | RRF birleştirme |
| Reranking | Entegrasyon | Testleri |
| LLM sohbet + prompt | Pair + backend testleri | `IChatClient` + pair |
| Blazor | Sohbet ekranı | Belge yönetimi sayfası |
| Test (xUnit, bUnit, Testcontainers) | İzolasyon, entegrasyon testleri | Auth, parser, arama, UI testleri |

---

## 7. Git Öğrenme Kontrol Listesi

Repoda ayrı bir issue olarak açılır. İkimiz de her maddeyi en az bir kez kendimiz yapmadan proje bitmez.

| Beceri | Dide | Erva |
|---|---|---|
| Merge conflict çözme | [ ] | [ ] |
| `git rebase -i` ile commit toparlama | [ ] | [ ] |
| `git revert` ile hatalı merge'ü geri alma | [ ] | [ ] |
| `git cherry-pick` | [ ] | [ ] |
| Tag ve release oluşturma | [ ] | [ ] |
| Başka birinin PR'ına "değişiklik iste" review'ı | [ ] | [ ] |

> Faz 1'deki paralel migration'lar ve Faz 2'deki paralel parser'lar doğal olarak conflict üretecek.

---

## 8. Riskler ve Önlemler

| Risk | Önlem |
|---|---|
| Yerel 7–8B modelin Türkçe cevap kalitesi zayıf kalabilir | `IChatClient` soyutlaması ile canlıda API modeline geçiş baştan planlı |
| Tenant verisinin sızması | İki katmanlı izolasyon (query filter + RLS) ve otomatik testler |
| LLM maliyetinin kontrolden çıkması | Tenant bazlı token kotası, rate limiting, her çağrının kaydı |
| Chunking veya model değişikliklerinin kaliteyi fark edilmeden bozması | Faz 2'den itibaren değerlendirme seti, her değişiklikte çalıştırılır |
| KVKK: belgelerin dış LLM sağlayıcısına gitmesi | Müşteriye veri akışı açıkça bildirilir; ileride "yerel model" seçeneği sunulur |
| Arka plan işlerinin yeniden başlatmada kaybolması | Belge durumu veritabanında, açılışta kurtarma |
