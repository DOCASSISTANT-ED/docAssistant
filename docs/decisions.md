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
| 20 | Toplu güncellemede tenant koruması | `ExecuteUpdate` / `ExecuteDelete` `SaveChanges`'i çağırmadığı için uygulamanın yazma kuralları bunlara uygulanmaz; query filter yalnızca etkilenen satırları sınırlar. Bir toplu güncellemeyle satırı başka tenant'a taşımayı RLS'in `WITH CHECK` kuralı engeller; uygulama katmanında ayrıca engellenmez. Toplu güncellemeler yaygınlaşırsa yeniden değerlendirilir. | 2026-10-06 |
| 21 | Belge okuma mimarisi | Her dosya türü kendi parser'ıyla okunur; parser'ların hepsi aynı ortak modele çevirir: sıralı bloklar (başlık + seviye, paragraf, tablo). Chunking tek algoritmadır ve yalnızca blokları görür. Dosyalar chunking öncesinde PDF'e dönüştürülmez; DOCX'in PDF kopyası Faz 4'te yalnızca önizleme için yeniden değerlendirilir. | 2026-10-06 |
| 22 | Sayfa numarası | Blokta ve chunk'ta boş olabilir. PDF'te doludur, DOCX'te boştur; DOCX kaynakları bölüm yoluyla gösterilir. | 2026-10-06 |
| 23 | PDF parser'ın Faz 2 kapsamı | Başlıklar yazı tipi boyutuna dayalı basit bir kuralla tahmin edilir. Tablolar algılanmaz, düz metin olarak gelir. Okunabilir metni olmayan (taranmış) PDF açık bir hatayla reddedilir; OCR MVP dışındadır. | 2026-10-06 |
| 24 | Parser hataları | Bozuk, parolalı, boş ya da metinsiz dosyalarda parser, kullanıcıya gösterilebilir nedeni taşıyan özel bir hata fırlatır. Arka plan işi belgeyi `Failed` yapar ve nedeni `documents.failure_reason` sütununa yazar. | 2026-10-06 |
| 25 | Metin temizliği | Satırları paragrafa birleştirme ve satır sonunda bölünmüş kelimeleri onarma parser'ın işidir. Tekrarlanan üst/alt bilgi ve sayfa numarası ayıklama Faz 2'de yapılmaz. | 2026-10-06 |
| 26 | Test belgeleri | Parser testleri `tests/` altındaki ortak örnek dosyaları kullanır. Örnekler bizim ürettiğimiz, içeriği bilinen belgelerdir (PDF ve DOCX); gerçek bir şirketin belgesi repoya konmaz. | 2026-10-06 |
| 27 | Chunk tutarlılığı ve ölçüm | Her chunk'ın başında en azından belge adı bulunur (bölüm yolu çıkarılabiliyorsa o da). Chunk boyutu kaynağın türüne göre değişmez. Chunk'a kaynak türü (PDF/DOCX) yazılır. Değerlendirme setinde iki tür birlikte ve aynı belgenin iki hâli de bulunur; sonuçlara türe göre ayrı bakılır. | 2026-10-06 |
| 28 | Embedding'in fazı | Faz 2'de chunk'lar embedding'siz kaydedilir; `embedding` ve `embedding_model` sütunları boş olabilir olarak açılır. Faz 3'te hem yeni belgeler hem mevcut chunk'lar için doldurulur. | 2026-10-06 |
| 29 | `chunks` tablosu | Sütunlar: `id`, `tenant_id`, `document_id`, `document_version`, `ordinal` (belge içindeki sıra), `content`, `page_number` (boş olabilir), `section_path` (boş olabilir), `source_type`, `embedding` ve `embedding_model` (Faz 3'e kadar boş), `created_at`. `ITenantOwned`'dur, RLS'i migration'da açılır, belge silinince chunk'ları da silinir. Tablo chunking ile birlikte (pair) eklenir. | 2026-10-06 |
| 30 | `documents` tablosuna eklenenler | `file_name`, `content_type`, `size_bytes`, `storage_key`, `version` (1'den başlar), `uploaded_by_user_id`, `failure_reason`, `processed_at`. Yükleme endpoint'i ile birlikte eklenir. | 2026-10-06 |
| 31 | Arka plan işinde tenant | HTTP isteği olmayan işler için tenant'ı kodla ayarlayan ikinci bir `ITenantContext` uygulaması yazılır; iş her belge için o belgenin tenant'ını seçer. Query filter, yazma kontrolü ve RLS değişmeden çalışır. Kuyruğa belge kimliğiyle birlikte tenant kimliği konur. | 2026-10-06 |
| 32 | Yükleme kuralları | Yalnızca PDF ve DOCX kabul edilir; tür uzantıya değil dosyanın içeriğine bakılarak doğrulanır. En fazla 20 MB. Yalnızca `Admin` yükleyebilir. Endpoint işlemenin bitmesini beklemez: belge kimliğini ve `Pending` durumunu hemen döner. Faz 2'de sürümleme yoktur: aynı adla ikinci yükleme yeni bir belgedir, her belge 1. sürümde kalır. | 2026-10-06 |
| 33 | Dosya saklama düzeni | Yol: `tenants/{tenantId}/documents/{documentId}/original.{pdf\|docx}`. Kullanıcının verdiği dosya adı yolda kullanılmaz, yalnızca veritabanında durur. Depolamaya resmi AWS S3 kütüphanesiyle, `IFileStorage` arayüzü (kaydet, oku, sil) üzerinden erişilir. | 2026-10-06 |
| 34 | Kuyruk sözleşmesi ve kurtarma | Yükleme ile arka plan işi `IIngestionQueue` arayüzüyle buluşur (belge + tenant kimliğini sıraya al). Açılışta `Pending` ve `Processing` belgeleri bütün tenant'lar için bulmak üzere, yalnızca bu belgelerin kimlik ve tenant bilgisini döndüren dar bir veritabanı fonksiyonu yazılır; uygulamaya süper kullanıcı bağlantısı verilmez. | 2026-10-06 |

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
- #20 gerekçesi: tenant izolasyon testleri sırasında bulundu. Uygulama bağlantısında RLS bu taşımayı reddediyor ve `RowLevelSecurityTests.BulkUpdateCannotMoveDocumentsToAnotherTenant` bunu güvence altına alıyor; şu an toplu güncelleme kullanan kod olmadığı için uygulama katmanına ek kod yazılmadı.
- #21 gerekçesi: DOCX belgenin yapısını (başlıklar, tablolar, paragraflar) açıkça taşır, PDF yalnızca görünüşünü. DOCX'i PDF'e çevirmek bu bilgiyi atıp tahminle geri kazanmaya çalışmak olurdu; ayrıca sunucuda LibreOffice gibi ağır bir bağımlılık gerektirir. PDF'in tek üstünlüğü sayfa numarası ve tarayıcıda gösterilebilmesidir; bu bir önizleme konusudur.
- #22 gerekçesi: bir DOCX dosyasında sayfa bilgisi saklanmaz; sayfalar dosya açılırken yazı tipine ve kâğıt boyutuna göre hesaplanır. Faz 4'teki "kaynağa tıklayınca ilgili sayfa açılır" davranışı DOCX için bölüme gider.
- #23 gerekçesi: PDF'te başlık ve tablo işaretli değildir, tahmin edilir. Basit kurallarla başlanır; iyileştirme değerlendirme setinin gösterdiği yere yapılır. Yanlış başlık tahmini belgeyi bozmaz, yalnızca bölüm yolunu eksik bırakır. Taranmış PDF'in sessizce "0 chunk, başarılı" sayılması en kötü sonuç olurdu.
- #27 gerekçesi: embedding modeli chunk'ın kaynağını bilmez, yalnızca metni okur. Risk, iki kaynağın chunk'larının biçimce farklılaşmasıdır (ör. yalnızca DOCX chunk'larının başlık taşıması aramada onları haksız yere öne çıkarır).
- #28 gerekçesi: Faz 2'nin bitiş koşulları embedding istemiyor. Faz 3'teki yeniden indeksleme işi mevcut chunk'ları da doldurur.
- #31 gerekçesi: arka planda JWT yoktur, `ITenantContext` boş gelir; yazma kontrolü ve RLS her şeyi reddeder. Arayüz bu yüzden baştan JWT'ye özel tutulmadı (#14).
- #32 gerekçesi: planın rol tanımına göre admin belge yükler, üye soru sorar. Sürümleme ayrı bir endpoint ve ayrı kararlar gerektirir; `version` sütunu şimdiden açılır ki sonradan eklemek şema değişikliği gerektirmesin.
- #33 gerekçesi: tenant yolun başında olduğu için bir firmanın dosyaları tek önek altında toplanır ve yanlış tenant'a erişim göze çarpar. Kullanıcı dosya adını yola koymamak, tuhaf karakterli ya da kötü niyetli adların depolamayı etkilemesini önler. S3 API'si SeaweedFS'te ve canlıdaki depolamada aynıdır.
- #34 gerekçesi: kurtarma sorgusu tenant seçilmeden bütün tenant'lara bakmak zorunda, yani hem query filter'ı hem RLS'i aşmalı. Bunu süper kullanıcı bağlantısıyla yapmak #19'u boşa çıkarırdı; dar bir fonksiyon yalnızca gereken iki sütunu açar.
