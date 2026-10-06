# Test Yazma Rehberi

Çapraz test kuralı geçerlidir: herkes karşı tarafın yazdığı kodun testini yazar ([PLANNING.md](../PLANNING.md)).

## Hangi proje?

| Proje | Ne için | Docker |
|---|---|---|
| `tests/DocAssistant.UnitTests` | Tek bir sınıfı tek başına sınamak: token üretimi, e-posta normalleştirme, doğrulama kuralları | Gerekmez |
| `tests/DocAssistant.IntegrationTests` | Parçaları birlikte sınamak: endpoint'ler, veritabanı, RLS, izinler | Gerekir |

Veritabanına dokunan her test entegrasyon testidir. Bellek içi veritabanı ya da SQLite kullanılmaz ([decisions.md](decisions.md) #18).

## Entegrasyon testi altyapısı

`tests/DocAssistant.IntegrationTests/Infrastructure/` altında:

| Sınıf | Ne yapar |
|---|---|
| `PostgresFixture` | `pgvector/pgvector:pg17` container'ını açar, `docker/postgres/init/` betiklerini çalıştırır (`docassistant_app` oluşur), tüm migration'ları uygular. API'yi de bu veritabanına bağlı olarak bellekte başlatır (`Api`). |
| `DocAssistantApiFactory` | Gerçek `Program.cs`'i `Testing` ortamında çalıştırır: test veritabanı (kısıtlı kullanıcı) ve testlere özel JWT anahtarı (`SigningKey`). User-secrets ve `appsettings.Development.json` okunmaz. |
| `DatabaseCollection` | `[Collection(DatabaseCollection.Name)]` taşıyan bütün test sınıfları aynı container'ı ve aynı API'yi paylaşır; container her `dotnet test`'te bir kez açılır. |

## Örnek: HTTP üzerinden test

```csharp
using System.Net;
using System.Net.Http.Json;
using DocAssistant.Api.Modules.Identity;
using DocAssistant.IntegrationTests.Infrastructure;

namespace DocAssistant.IntegrationTests.Identity;

[Collection(DatabaseCollection.Name)]
public class RegisterTests(PostgresFixture database)
{
    [Fact]
    public async Task RegisterReturnsToken()
    {
        using var client = database.Api.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.com";

        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            companyName = "Test Firma",
            email,
            password = "Gizli123!",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<AccessToken>();
        Assert.False(string.IsNullOrEmpty(token!.Token));
    }
}
```

## Kurallar

1. **Veritabanı testler arasında paylaşılır ve sıfırlanmaz.** Her test kendi verisini oluşturur ve benzersiz değerler kullanır (ör. `Guid.NewGuid()` ile e-posta). "Tabloda tek satır var" gibi tablonun tamamına dair varsayım yapma; başka testlerin verisi de oradadır.
2. **İzolasyonu süper kullanıcıyla sınama.** `CreateOwnerDbContext()` ve `SuperuserConnectionString` RLS'i atlar; yalnızca hazırlık ve kontrol içindir. Firma izolasyonunu API üzerinden (`database.Api`) ya da `AppConnectionString` ile (kısıtlı kullanıcı) sına.
3. **Token gerekiyorsa** API'nin kendi servisini kullan: `database.Api.Services.GetRequiredService<JwtTokenIssuer>()`. Sahte ya da yanlış anahtarla imzalanmış token üretmek için anahtar `DocAssistantApiFactory.SigningKey`'dir.
4. **Farklı bir servis gerekiyorsa** (ör. saati kontrol etmek için sahte bir `TimeProvider`) ortak API'yi değiştirme; ondan türetilmiş bir kopya oluştur:

   ```csharp
   // using Microsoft.AspNetCore.TestHost;
   // using Microsoft.Extensions.DependencyInjection;
   using var api = database.Api.WithWebHostBuilder(builder =>
       builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(fakeTimeProvider)));
   using var client = api.CreateClient();
   ```

5. **Yazdığın testin bir şeyi gerçekten koruduğunu gör:** test edilen davranışı geçici olarak boz, testin kırmızıya döndüğünü kontrol et, sonra geri al.

## Altyapının kendi testleri

`Infrastructure/*Tests.cs` altyapının doğru kurulduğunu sınar: tüm migration'lar uygulanmış mı, `docassistant_app` süper kullanıcı değil mi ve RLS'i atlayamıyor mu, her tabloya izni var mı, API test veritabanına kısıtlı kullanıcıyla ve test anahtarıyla mı bağlanıyor. Bunlardan biri kırmızıysa önce onu düzelt; diğer testlerin sonuçları güvenilir değildir.
