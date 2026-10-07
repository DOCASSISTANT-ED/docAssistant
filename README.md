# DocAssistant

KOBİ'ler için çok kiracılı (multi-tenant) Türkçe doküman asistanı. Firmalar kendi belgelerini yükler, çalışanlar bu belgelere soru sorar, cevaplar kaynak gösterilerek gelir.

- Proje planı ve mimari: [PLANNING.md](PLANNING.md)
- Kararlar: [docs/decisions.md](docs/decisions.md)

## Gereksinimler

| Araç | Sürüm | Not |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0.100 veya üstü (10.0.x) | Sürüm `global.json` ile sabitlenir |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | Güncel | PostgreSQL ve SeaweedFS bunun içinde çalışır |
| [Node.js](https://nodejs.org/) | 24 LTS (en az 24.15) ya da 22 LTS (en az 22.22) | Angular arayüzü (`src/web`) için; npm onunla birlikte gelir |
| [Ollama](https://ollama.com/download) | Güncel | Windows'a doğrudan kurulur, Docker içinde değil |
| Git | Güncel | |

## Kurulum

Komutlar proje klasöründe çalıştırılır.

### 1. Repoyu klonla

```bash
git clone https://github.com/DOCASSISTANT-ED/docAssistant.git
cd docAssistant
```

### 2. Ortam dosyasını oluştur

`.env.example` dosyasını `.env` adıyla kopyala. `.env` commit edilmez; içindeki değerler yalnızca kendi bilgisayarın içindir.

```bash
cp .env.example .env
```

PowerShell'de: `Copy-Item .env.example .env`

### 3. Servisleri başlat

Docker Desktop açık olmalı.

```bash
docker compose up -d
```

Bu komut iki servis başlatır:

| Servis | Ne için | Adres |
|---|---|---|
| PostgreSQL 17 + pgvector | Veritabanı | `localhost:5432` |
| SeaweedFS | S3 uyumlu dosya saklama; `documents` bucket'ı açılışta otomatik oluşur | S3 API: http://localhost:8333, yönetim paneli: http://localhost:23646 |

Doğrulamak için:

```bash
docker compose ps
docker compose exec postgres psql -U docassistant -d docassistant -c "SELECT '[1,2,3]'::vector <-> '[1,2,4]'::vector AS distance;"
```

İlk komutta `docassistant-postgres` ve `docassistant-seaweedfs` satırlarında `healthy` görünmeli, ikincisi `1` döndürmelidir.

API dosya depolamaya `appsettings.Development.json` içindeki `Storage` ayarlarıyla bağlanır (adres, erişim anahtarı, gizli anahtar, bucket). Bu değerler `.env.example`'daki `S3_` değerleriyle aynıdır; `.env` dosyanda `S3_` değerlerini değiştirirsen `Storage` ayarlarını da aynı yap.

### 4. Embedding modelini indir

Ollama kurulduktan sonra yeni bir terminal aç (eski terminaller `ollama` komutunu tanımaz) ve modeli indir. Yaklaşık 1,2 GB.

```bash
ollama pull bge-m3
ollama list
```

Listede `bge-m3:latest` görünmelidir.

### 5. Derle ve test et

```bash
dotnet build
dotnet test
```

Docker Desktop açık olmalı: entegrasyon testleri ([Testcontainers](https://dotnet.testcontainers.org/) ile) kendi geçici PostgreSQL container'larını açar, migration'ları uygular ve bitince siler. Geliştirme veritabanına (`docker compose`) dokunmazlar. Test yazma rehberi: [docs/testing.md](docs/testing.md).

### 6. Veritabanı şemasını oluştur

Tablolar EF Core migration'larıyla oluşturulur. İlk komut `dotnet-ef` aracını `dotnet-tools.json`'daki sürümle kurar, ikincisi bekleyen migration'ları veritabanına uygular. PostgreSQL container'ı çalışıyor olmalı.

```bash
dotnet tool restore
dotnet ef database update --project src/DocAssistant.Api
```

Doğrulamak için:

```bash
docker compose exec postgres psql -U docassistant -d docassistant -c "\dt" -c "\du"
```

Tablo listesinde `tenants`, `documents` ve `__EFMigrationsHistory`, kullanıcı listesinde `docassistant` ve `docassistant_app` görünmelidir.

Veritabanında iki kullanıcı vardır:

| Kullanıcı | Kim kullanır | Bağlantı cümlesi | Neden |
|---|---|---|---|
| `docassistant` (süper kullanıcı) | Yalnızca `dotnet ef` komutları | `Migrations` | Tablo, yetki ve RLS politikası oluşturabilmek için |
| `docassistant_app` (kısıtlı) | Çalışan API | `Default` | Süper kullanıcı Row-Level Security'den muaftır; API onunla bağlansaydı tenant izolasyonunun veritabanı katmanı hiç çalışmazdı |

`docassistant_app`, veritabanı ilk oluşturulurken `docker/postgres/init/02-app-user.sh` ile kendiliğinden oluşur. Geliştirmede iki bağlantı cümlesi de `appsettings.Development.json` içindedir.

### 7. JWT imza anahtarını ayarla

API, giriş token'larını imzalamak için gizli bir anahtar kullanır. Anahtar repoda tutulmaz; her geliştirici kendi bilgisayarında [user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) ile bir kez ayarlar. Anahtar yoksa API açılmaz ve `SigningKey` hatası verir.

Rastgele bir anahtar üret (Git Bash):

```bash
openssl rand -base64 64
```

PowerShell'de:

```powershell
$b = New-Object byte[] 64; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
```

Çıkan değeri kaydet:

```bash
dotnet user-secrets set "Jwt:SigningKey" "<üretilen-anahtar>" --project src/DocAssistant.Api
```

### 8. Arayüzün paketlerini kur

Arayüz, `src/web` altında ayrı bir Angular uygulamasıdır. Paketleri bir kez (ve `package-lock.json` her değiştiğinde) kurulur:

```bash
cd src/web
npm install
```

### 9. Uygulamayı çalıştır

API ve arayüz ayrı uygulamalardır; her biri kendi terminalinde çalıştırılır.

Proje klasöründe:

```bash
dotnet run --project src/DocAssistant.Api
```

`src/web` klasöründe:

```bash
npm start
```

| Uygulama | Adres | Kontrol |
|---|---|---|
| API | http://localhost:5174 | http://localhost:5174/health `Healthy` döndürür |
| Arayüz | http://localhost:4200 | Sayfanın üstünde "API çalışıyor" yazar |

Arayüz API'ye tarayıcıdan, başka bir adresten istek gönderir. API buna yalnızca izin verdiği adresler için razı olur (CORS); geliştirmede izinli adres `appsettings.Development.json` içindeki `Cors:AllowedOrigins` listesindedir (`http://localhost:4200`).

## Günlük kullanım

| İş | Komut |
|---|---|
| Servisleri başlat | `docker compose up -d` |
| Servisleri durdur (veri kalır) | `docker compose down` |
| Servisleri sıfırla (veritabanı ve yüklenen dosyalar silinir) | `docker compose down -v` |
| Testleri çalıştır | `dotnet test` |
| Arayüz testlerini çalıştır (`src/web` içinde) | `npm test` |
| API'yi elle dene (kayıt, giriş, `/auth/me`) | `src/DocAssistant.Api/DocAssistant.Api.http`; VS Code'da [REST Client](https://marketplace.visualstudio.com/items?itemName=humao.rest-client) eklentisiyle her isteğin üstündeki "Send Request" |
| Bekleyen migration'ları uygula (`git pull` sonrası) | `dotnet ef database update --project src/DocAssistant.Api` |
| Yeni migration üret | `dotnet ef migrations add <Ad> --project src/DocAssistant.Api --output-dir Data/Migrations` |

## Sık karşılaşılan sorunlar

- **`ollama` komutu tanınmıyor:** Terminal Ollama kurulmadan önce açılmıştır. Terminali, VS Code kullanıyorsan VS Code'u tamamen kapatıp yeniden aç.
- **`docker compose up` bağlanamıyor:** Docker Desktop çalışmıyordur; açıp motorun başlamasını bekle.
- **Entegrasyon testleri "Docker is either not running or misconfigured" hatasıyla başarısız oluyor:** Aynı sebep; Docker Desktop'ı açıp `dotnet test`'i yeniden çalıştır. Birim testleri Docker'a ihtiyaç duymaz.
- **Arayüzde "API'ye ulaşılamıyor" yazıyor:** API çalışmıyordur (`dotnet run --project src/DocAssistant.Api`) ya da arayüz 4200 dışında bir porttan açılmıştır; API yalnızca `Cors:AllowedOrigins` listesindeki adreslere izin verir. Tarayıcı konsolunda "CORS" hatası görürsen ikinci durumdur.
- **`npm start` "ng tanınmıyor" ya da modül bulunamadı hatası veriyor:** Paketler kurulmamıştır; `src/web` içinde `npm install` çalıştır.
- **API açılışta `The SigningKey field is required` hatası veriyor:** Bu bilgisayarda JWT anahtarı ayarlanmamıştır; 7. adımı uygula.
- **5432 portu kullanımda:** Bilgisayarında başka bir PostgreSQL çalışıyordur. `.env` dosyasında `POSTGRES_PORT` değerini değiştir. 8333 veya 23646 portları için aynı şekilde `S3_PORT` ve `SEAWEEDFS_ADMIN_PORT` kullanılır.
- **`.env` dosyan bu değişiklikten önce oluşturulduysa:** `.env.example`'daki `S3_` ve `SEAWEEDFS_` satırlarını kendi `.env` dosyana ekle; yoksa SeaweedFS anahtarsız açılır.
- **Migration `role "docassistant_app" does not exist` hatası veriyor:** Veritabanın bu kullanıcı eklenmeden önce oluşturulmuştur; init betikleri yalnızca veritabanı ilk oluşurken çalışır. `.env` dosyana `.env.example`'daki `APP_DB_PASSWORD` satırını ekle, sonra veriyi silmeden kullanıcıyı oluştur:

  ```bash
  docker compose up -d
  docker compose exec postgres bash /docker-entrypoint-initdb.d/02-app-user.sh
  ```

  Ardından `dotnet ef database update --project src/DocAssistant.Api` komutunu yeniden çalıştır.
- **API bir sorguda `permission denied for table ...` hatası veriyor:** Migration'lar uygulanmamıştır; tablo yetkilerini migration'lar verir. `dotnet ef database update --project src/DocAssistant.Api` çalıştır.
- **Dosya kaydederken `The request signature we calculated does not match` hatası:** API'nin `Storage:AccessKey` / `Storage:SecretKey` ayarları SeaweedFS'in açıldığı `S3_ACCESS_KEY` / `S3_SECRET_KEY` değerleriyle aynı değildir (`.env` ile `appsettings.Development.json`'ı karşılaştır).
- **API açılışta `Storage` ile ilgili bir doğrulama hatası veriyor:** Çalıştığı ortamda `Storage` ayarları tanımlı değildir; geliştirmede `appsettings.Development.json`'dan gelir.
- **`dotnet ef` komutu tanınmıyor:** `dotnet tool restore` çalıştırılmamıştır.
- **API açılışta "Connection string 'Default' is not configured" hatası veriyor:** Uygulama `Development` ortamında çalışmıyordur; bağlantı cümlesi `appsettings.Development.json` içindedir. `dotnet run` bunu kendiliğinden ayarlar.
- **İlk embedding isteği yavaş:** Model belleğe yüklenirken ilk istek yarım dakika kadar sürebilir; sonrakiler hızlıdır.

## Katkı

- `main` korumalıdır; her iş kendi branch'inde yapılır ve PR ile, merge commit yöntemiyle birleştirilir.
- Commit mesajları [Conventional Commits](https://www.conventionalcommits.org/) formatındadır (`feat:`, `fix:`, `test:`, `docs:`, `chore:`).
- Çapraz test kuralı geçerlidir: herkes karşı tarafın yazdığı kodun testini yazar.

Ayrıntılar için [PLANNING.md](PLANNING.md) dosyasına bakın.
