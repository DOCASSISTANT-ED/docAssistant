# DocAssistant

KOBİ'ler için çok kiracılı (multi-tenant) Türkçe doküman asistanı. Firmalar kendi belgelerini yükler, çalışanlar bu belgelere soru sorar, cevaplar kaynak gösterilerek gelir.

- Proje planı ve mimari: [PLANNING.md](PLANNING.md)
- Kararlar: [docs/decisions.md](docs/decisions.md)

## Gereksinimler

| Araç | Sürüm | Not |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0.100 veya üstü (10.0.x) | Sürüm `global.json` ile sabitlenir |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | Güncel | PostgreSQL ve SeaweedFS bunun içinde çalışır |
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

### 6. JWT imza anahtarını ayarla

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

### 7. Uygulamayı çalıştır

API ve Web ayrı projelerdir; her biri kendi terminalinde çalıştırılır.

```bash
dotnet run --project src/DocAssistant.Api
```

```bash
dotnet run --project src/DocAssistant.Web
```

| Uygulama | Adres | Kontrol |
|---|---|---|
| API | http://localhost:5174 | http://localhost:5174/health `Healthy` döndürür |
| Web | http://localhost:5078 | Ana sayfa açılır |

## Günlük kullanım

| İş | Komut |
|---|---|
| Servisleri başlat | `docker compose up -d` |
| Servisleri durdur (veri kalır) | `docker compose down` |
| Servisleri sıfırla (veritabanı ve yüklenen dosyalar silinir) | `docker compose down -v` |
| Testleri çalıştır | `dotnet test` |

## Sık karşılaşılan sorunlar

- **`ollama` komutu tanınmıyor:** Terminal Ollama kurulmadan önce açılmıştır. Terminali, VS Code kullanıyorsan VS Code'u tamamen kapatıp yeniden aç.
- **`docker compose up` bağlanamıyor:** Docker Desktop çalışmıyordur; açıp motorun başlamasını bekle.
- **5432 portu kullanımda:** Bilgisayarında başka bir PostgreSQL çalışıyordur. `.env` dosyasında `POSTGRES_PORT` değerini değiştir. 8333 veya 23646 portları için aynı şekilde `S3_PORT` ve `SEAWEEDFS_ADMIN_PORT` kullanılır.
- **`.env` dosyan bu değişiklikten önce oluşturulduysa:** `.env.example`'daki `S3_` ve `SEAWEEDFS_` satırlarını kendi `.env` dosyana ekle; yoksa SeaweedFS anahtarsız açılır.
- **İlk embedding isteği yavaş:** Model belleğe yüklenirken ilk istek yarım dakika kadar sürebilir; sonrakiler hızlıdır.

## Katkı

- `main` korumalıdır; her iş kendi branch'inde yapılır ve PR ile, merge commit yöntemiyle birleştirilir.
- Commit mesajları [Conventional Commits](https://www.conventionalcommits.org/) formatındadır (`feat:`, `fix:`, `test:`, `docs:`, `chore:`).
- Çapraz test kuralı geçerlidir: herkes karşı tarafın yazdığı kodun testini yazar.

Ayrıntılar için [PLANNING.md](PLANNING.md) dosyasına bakın.
