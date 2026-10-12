# Teknoloji Yığını

DocAssistant'ta kullanılan teknolojiler, ne işe yaradıkları ve hangi fazda devreye girdikleri.
Kaynak: [PLANNING.md](../PLANNING.md) ve [decisions.md](decisions.md). Buradaki bir satır o iki dosyayla çelişirse onlar geçerlidir.

Durum sütunu: **Kurulu** = repoda var, **Planlı** = kararı verilmiş ama henüz eklenmemiş, **Seçilecek** = karar bekliyor.

## Özet

| Katman | Teknoloji |
|---|---|
| Dil / çalışma zamanı | C#, .NET 10 (arka uç); TypeScript, Node.js (arayüz) |
| Backend | ASP.NET Core Web API |
| Arayüz | Angular |
| Veritabanı | PostgreSQL 17 + pgvector |
| Veri erişimi | EF Core |
| Dosya saklama | SeaweedFS (geliştirme), S3 uyumlu depolama (canlı) |
| Yapay zekâ | Ollama, bge-m3 (embedding), `IChatClient` / `IEmbeddingGenerator` |
| Test | xUnit, Testcontainers, Angular test araçları |
| Altyapı | Docker Compose, GitHub Actions |

Mimari: modüler monolit. Mikroservis yok.

## Platform ve dil

| Teknoloji | Sürüm | Ne için | Durum |
|---|---|---|---|
| .NET SDK | 10.0.100+ (`global.json`, `rollForward: latestFeature`) | Tüm projelerin derlenmesi ve çalışması | Kurulu |
| C# | .NET 10 ile gelen sürüm | Uygulama dili | Kurulu |
| Hedef çatı | `net10.0` (`Directory.Build.props`) | Tüm projelerde ortak | Kurulu |

Ortak derleme ayarları `Directory.Build.props` içinde: nullable açık, uyarılar hata sayılır, kod stili derlemede denetlenir.
Paket sürümleri yalnızca `Directory.Packages.props` içinde tutulur (merkezi paket yönetimi); `.csproj` dosyalarına sürüm yazılmaz.

## Backend

| Teknoloji | Ne için | Faz | Durum |
|---|---|---|---|
| ASP.NET Core Web API (`DocAssistant.Api`) | HTTP uç noktaları: kimlik, yükleme, sohbet | 0 | Kurulu |
| `Microsoft.AspNetCore.OpenApi` 10.0.12 | API'nin OpenAPI tanımını üretir | 0 | Kurulu |
| JWT kimlik doğrulama (`Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.12) | Kayıt, giriş, istekte kullanıcı ve tenant bilgisinin taşınması | 1 | Kurulu |
| `BackgroundService` + `Channel` | Belge işleme kuyruğu (ingestion): tek tek işleme, en fazla 100 bekleyen mesaj, açılışta yarım işleri kurtarma (decisions.md #34, #39) | 2 | Kurulu |
| Rate limiting ve token kotası | Tenant bazlı kullanım sınırı | 5 | Planlı |

## Arayüz

| Teknoloji | Ne için | Faz | Durum |
|---|---|---|---|
| Angular (`src/web`) | Giriş ve kayıt, sohbet ekranı, belge yönetimi, yönetim paneli. API ile HTTP üzerinden konuşan ayrı bir uygulama; SSR yok | 2 (iskelet), 4–5 (ekranlar) | Kurulu (iskelet) |
| TypeScript | Arayüzün dili | 2 | Kurulu |
| Node.js + npm | Arayüzün derlenmesi, paketleri ve geliştirme sunucusu. Node 24 LTS | 2 | Kurulu |

Arayüz başlangıçta Blazor Web App olarak planlanmıştı; ekranlar yazılmadan Angular'a geçildi (decisions.md #41). Şablon hâlindeki Blazor projesi (`src/DocAssistant.Web`) Angular iskeletiyle birlikte kaldırıldı.

## Veri

| Teknoloji | Sürüm | Ne için | Faz | Durum |
|---|---|---|---|---|
| PostgreSQL | 17 (`pgvector/pgvector:pg17` imajı) | Ana veritabanı | 0 | Kurulu |
| pgvector | 0.8+ | Vektör saklama ve benzerlik araması; filtreli aramada HNSW indeksinin verimli çalışması için 0.8+ gerekli | 0 (eklenti), 3 (arama) | Kurulu |
| Pgvector.EntityFrameworkCore | 0.3.0 | C#'taki `Vector` türünü `vector` sütununa bağlar; `chunks.embedding` (decisions.md #28) | 2 (sütun), 3 (doldurma) | Kurulu |
| EF Core + migration | 10 (`Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3) | Veri erişimi, şema değişiklikleri, global query filter | 1 | Kurulu |
| Row-Level Security | PostgreSQL özelliği | Tenant izolasyonunun veritabanı katmanı; `documents` ve `chunks` tablolarında açık | 1 | Kurulu |
| Full-text arama (`turkish`) | PostgreSQL özelliği | Anahtar kelime araması: `chunks.search_vector` (hesaplanan sütun, GIN indeksi), kelimelerden herhangi biri eşleşir, `ts_rank_cd` ile sıralanır (decisions.md #49) | 3 | Kurulu |
| SeaweedFS | 4.48 (`chrislusf/seaweedfs`, `mini` modu) | Yüklenen dosyaların S3 API'si üzerinden saklanması (geliştirme). MinIO yerine, bkz. decisions.md #9 | 0 (compose), 2 (kullanım) | Kurulu |
| AWSSDK.S3 | 4.0.104 | Depolamaya S3 API'siyle erişim; `IFileStorage` arkasında, kod SeaweedFS'e özel değil (decisions.md #33) | 2 | Kurulu |
| S3 uyumlu depolama | — | Dosya saklama (canlı) | 5 | Planlı |

Tenant izolasyonu iki katmanlıdır: uygulamada EF Core global query filter, veritabanında Row-Level Security.

## Belge işleme (ingestion)

| Teknoloji | Ne için | Faz | Durum |
|---|---|---|---|
| PdfPig 0.1.16 | PDF'ten metin, sayfa numarası ve yapı çıkarma; başlıklar yazı boyutundan tahmin edilir (decisions.md #23) | 2 | Kurulu |
| OpenXML SDK (`DocumentFormat.OpenXml` 3.5.1) | DOCX okuma: başlıklar, paragraflar, tablolar | 2 | Kurulu |
| ClosedXML | XLSX okuma | 2 sonrası | Planlı |
| Kendi chunking algoritmamız | Yapıya göre bölme (bölüm → paragraf → cümle), Türkçe kısaltma desteği, en fazla 1500 karakter (decisions.md #43) | 2 | Kurulu |

Taranmış PDF için OCR bilinçli olarak MVP dışında.

## Yapay zekâ ve arama

| Teknoloji | Ne için | Faz | Durum |
|---|---|---|---|
| Ollama | Yerel model çalıştırma. Geliştirmede Windows'a doğrudan kurulur; compose'daki servis yalnızca `linux-gpu` profilinde çalışır | 0 | Planlı |
| bge-m3 | Embedding modeli (metni vektöre çevirir) | 0 (deneme), 3 (hat) | Planlı |
| `IEmbeddingGenerator` | Embedding modelini koddan soyutlar | 3 | Planlı |
| `IChatClient` | Sohbet modelini soyutlar; geliştirmede Ollama, canlıda API modeli | 4 | Planlı |
| HNSW indeksi (pgvector) | Hızlı vektör araması | 3 | Planlı |
| Reciprocal Rank Fusion (RRF) | Vektör ve full-text sonuçlarını tek sıralamada birleştirir | 3 | Planlı |
| Reranker modeli | İlk ~30 sonucu en iyi 5–8'e indirir | 3 | Seçilecek |
| Sohbet modeli (geliştirme) | Yerel 7–8B sınıfı bir model | 4 | Seçilecek |
| Sohbet modeli (canlı) | API üzerinden bir model | 4–5 | Seçilecek |

## Test

| Teknoloji | Ne için | Faz | Durum |
|---|---|---|---|
| xUnit 2.9.3 | Birim ve entegrasyon testleri | 0 (boş test) | Kurulu |
| Vitest (Angular'ın varsayılan test düzeni) | Arayüz bileşen testleri; `npm test` | 2 (iskelet), 4 (ekran testleri) | Kurulu |
| Testcontainers (`Testcontainers.PostgreSql` 4.15.0) | Testlerde gerçek PostgreSQL'i container olarak ayağa kaldırma; RLS testleri için zorunlu | 1 | Kurulu |
| Değerlendirme seti (`eval/questions.json`) | Arama ve cevap kalitesini ölçen soru-cevap çiftleri: 5 örnek belge, 96 soru (decisions.md #40, #45); setin çalıştırılması Faz 3'te | 2'den itibaren | Kurulu (soru seti) |

Çapraz test kuralı her fazda geçerlidir: herkes karşı tarafın yazdığı kodun testini yazar.

## Altyapı ve araçlar

| Teknoloji | Ne için | Faz | Durum |
|---|---|---|---|
| Docker Compose | Geliştirme ortamı: PostgreSQL, SeaweedFS, (Linux'ta) Ollama | 0 | Kurulu (PostgreSQL, SeaweedFS) |
| GitHub Actions | CI: iki paralel iş. `Build`: .NET derleme ve testler. `Web`: arayüzün derlenmesi ve testleri | 0 | Kurulu |
| Docker | Canlı ortama deploy | 5 | Planlı |
| `.editorconfig` | Ortak kod stili | 0 | Kurulu |
| Git + GitHub | `main` korumalı, `feature/*` branch'leri, en az 1 onaylı PR, Conventional Commits | 0 | Kurulu |

## Bilinçli olarak kullanılmayanlar

- Mikroservis mimarisi (modüler monolit seçildi)
- OCR
- Teams / Slack entegrasyonu, Google Drive senkronu
- Ödeme altyapısı
