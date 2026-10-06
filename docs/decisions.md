# Proje Kararları

Başlangıç toplantısında alınan, sonradan değiştirmesi zor kararlar. Değişiklikler PR ile yapılır.

| # | Konu | Karar | Tarih |
|---|------|-------|-------|
| 1 | Proje adı / namespace | `DocAssistant` | 2026-10-01 |
| 2 | Repo sahibi | Organizasyon hesabı: `DOCASSISTANT-ED` (repo: `docAssistant`, public) | 2026-10-01 |
| 3 | Teknoloji sürümleri | .NET 10, Blazor Web App, PostgreSQL 17 + pgvector 0.8+ | 2026-10-01 |
| 4 | Branch stratejisi | `main` korumalı; `feature/...` branch + en az 1 onaylı PR | 2026-10-01 |
| 5 | Merge yöntemi | Merge commit (squash ve rebase kullanılmaz) | 2026-10-05 |
| 6 | Commit formatı | Conventional Commits (`feat:`, `fix:`, `test:`, `docs:`, `chore:`) | 2026-10-01 |
| 7 | "Bitti" tanımı | Test yazılmış (çapraz test kuralına göre), CI yeşil, karşı taraf review edip onaylamış, gerekiyorsa README veya dokümantasyon güncellenmiş | 2026-10-01 |
| 8 | Embedding modeli | `bge-m3` (Ollama), 1024 boyutlu vektör | 2026-10-01 |
| 9 | Dosya saklama | SeaweedFS (geliştirme), S3 uyumlu depolama (canlı). MinIO'dan vazgeçildi, gerekçe Notlar'da. | 2026-10-05 |
| 10 | Sohbet modeli | Geliştirmede Ollama, canlıda API modeli (`IChatClient` ile değiştirilebilir) | 2026-10-01 |
| 11 | Veritabanı erişimi | Tek `AppDbContext`; bütün tablolar ve migration'lar tek yerde. Modül başına ayrı `DbContext` kullanılmaz. | 2026-10-05 |
| 12 | Kullanıcı–firma ilişkisi | Bir kullanıcı birden fazla firmaya üye olabilir (`users`, `tenants`, `memberships`). E-posta sistemde benzersizdir. Rol (`Admin`, `Member`) üyeliğe aittir; bir firmada birden fazla admin olabilir. | 2026-10-05 |
| 13 | Çoklu üyeliğin kapsamı | Faz 1'de yalnızca veri modeli kurulur: her kullanıcının tek üyeliği olur ve giriş doğrudan o firmaya yapılır. Firma seçme ve değiştirme ekranları ihtiyaç doğunca eklenir. | 2026-10-05 |
| 14 | Tenant sözleşmesi | `ITenantContext` (`DocAssistant.Shared`), `Guid? TenantId`. Değer bu istekte seçili firmayı gösterir; JWT'deki `tenant_id` claim'inden okunur. Boşken filtreli sorgular hiçbir satır döndürmez. Yazma kontrolü yalnızca `ITenantOwned` ile işaretli firma verisi tablolarında geçerlidir: `TenantId` otomatik doldurulur, tenant boşken kayıt eklemek hata verir. Kimlik tabloları (`tenants`, `users`, `memberships`) bu işareti taşımaz; kayıt işlemi bu tablolara tenant bilinmeden yazar. | 2026-10-05 |
| 15 | RLS kapsamı | Row-Level Security yalnızca firma verisi tablolarında açıktır (belgeler, chunk'lar, sohbetler). Kimlik tablolarında (`users`, `memberships`, `tenants`) kapalıdır; bunları global query filter korur: `tenants` → `Id` şu anki firma; `memberships` → `TenantId` şu anki firma; `users` → şu anki firmada üyeliği olanlar. Filtreler `AppDbContext`'te tanımlanır. Giriş ve kayıt, tenant bilinmeden arama yaptıkları yerlerde filtreyi bilerek atlar (`IgnoreQueryFilters`). | 2026-10-05 |
| 16 | Klasör düzeni | Modüller ayrı proje değil, `DocAssistant.Api` içinde klasördür: `Data/` (`AppDbContext`, migration'lar), `Modules/Tenants/`, `Modules/Identity/` vb. | 2026-10-05 |
| 17 | Faz 1'de RLS tablosu | `documents` tablosunun yalın hâli (kimlik, tenant, başlık, durum) Faz 1'e çekilir; RLS ve izolasyon testleri bu tablo üzerinde kurulur. | 2026-10-05 |
| 18 | Entegrasyon testlerinde veritabanı | Testcontainers Faz 5'ten Faz 1'e çekilir. Testler `pgvector/pgvector:pg17` imajıyla açılan gerçek PostgreSQL'e bağlanır; bellek içi veritabanı ya da SQLite kullanılmaz. | 2026-10-05 |
| 19 | Kısıtlı veritabanı kullanıcısı | Uygulama kullanıcısı (`docassistant_app`) `docker/postgres/init/` altındaki init betiğiyle oluşturulur; parolası `.env`'den gelir. Tablo yetkileri ve RLS politikaları migration'larla verilir. Testcontainers aynı init betiğini kullanır. | 2026-10-05 |

## Notlar

- Geliştirmede Ollama doğrudan Windows'a kurulur; compose'daki `ollama` servisi yalnızca `linux-gpu` profilinde çalışır.
- #9 gerekçesi: MinIO Community Edition Aralık 2025'te bakım moduna alındı, Nisan 2026'da arşivlendi; resmi Docker imajları artık yayınlanmıyor ve Docker Hub'dan çekilemeyebiliyor.
- #11 gerekçesi: kayıt sırasında firma, ilk kullanıcı ve üyelik tek transaction içinde yazılmalı; tek context'te bu tek bir `SaveChanges` çağrısıdır.
- #12–13 gerekçesi: veri modelini sonradan değiştirmek zor, ekran eklemek kolay. Üyelik tablosu baştan kurulur, çoklu firma ekranları ertelenir.
- #15 gerekçesi: giriş anında firma bilinmediği için kullanıcı tablosunda RLS açık olsaydı kimse giriş yapamazdı.
- #15 için kritik: PostgreSQL'de süper kullanıcılar ve tablo sahibi RLS'ten muaftır. Compose'daki `docassistant` kullanıcısı süper kullanıcıdır; uygulama onunla bağlanırsa politikalar hiç uygulanmaz. Migration'lar süper kullanıcıyla, uygulama yetkileri kısıtlı ayrı bir kullanıcıyla bağlanır.
- #14 gerekçesi: kayıt isteğinde henüz giriş yapılmadığı için tenant boştur; yazma kontrolü kimlik tablolarına da uygulansaydı firma, kullanıcı ve üyelik eklenemezdi.
- #18 gerekçesi: RLS bir PostgreSQL özelliğidir; başka bir veritabanında izolasyon testleri RLS'i hiç sınamadan geçer.
- #19 gerekçesi: parola migration koduna yazılmaz, çünkü migration'lar repoda durur. Init betikleri yalnızca volume ilk oluşturulurken çalışır; mevcut bir veritabanında betik bir kez elle çalıştırılır (`docker compose exec postgres bash /docker-entrypoint-initdb.d/02-app-user.sh`), volume'u sıfırlamak gerekmez. Uygulama `Default`, `dotnet ef` komutları `Migrations` bağlantı cümlesini kullanır.
- #16 gerekçesi: tek `AppDbContext` hem modüllerin tablolarını bilmek hem de modüller tarafından kullanılmak zorunda; ayrı projelerde bu döngüsel başvuruya yol açar.
