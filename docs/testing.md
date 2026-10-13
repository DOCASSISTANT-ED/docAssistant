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

## Arayüz testleri (Angular)

Arayüzün testleri `src/web` altındadır ve API'den bağımsız çalışır: veritabanı, Docker ya da çalışan bir API gerekmez. Çapraz test kuralı burada da geçerlidir.

```
cd src/web
npm test                    # dosyalar değiştikçe yeniden çalışır
npm test -- --watch=false   # bir kez çalışır (CI böyle çalıştırır)
```

| Parça | Ne yapar |
|---|---|
| Vitest | Testleri çalıştırır (`describe`, `it`, `expect`, sahte fonksiyon için `vi.fn()`). |
| `TestBed` | Bir servisi ya da ekranı test için kurar; `providers` ile istenen parça sahtesiyle değiştirilir. |
| `HttpTestingController` | API'nin yerini tutar: kodun gönderdiği isteği yakalar, test cevabı verir (`flush`). |
| `RouterTestingHarness` | Bir ekranı yönlendiricinin açtığı gibi açar; adres, yol korumaları ve bağlantılar gerçek çalışır. |

Test dosyası, sınadığı dosyanın yanında durur ve aynı adı taşır: `session.ts` → `session.spec.ts`.

### Neyi nasıl sınarız

- **Servisler ve interceptor'lar** (`session.spec.ts`, `api-request.interceptor.spec.ts`): gerçek `HttpClient`, sahte API. Test "API şunu cevaplarsa" der ve sonucu okur. `afterEach(() => api.verify())` beklenmeyen bir isteğin kalmadığını denetler.
- **Ekranlar** (`login.spec.ts`, `shell.spec.ts`): ekran kullanıcının kullandığı gibi sürülür: alana yaz, düğmeye bas, ekranda yazanı oku. Oturum servisi sahtedir; API'yle konuşma kısmı servisin kendi testindedir. Bileşenin iç alanlarına (`form`, `failure`) testten bakılmaz; kullanıcının göremediği bir şeyi test de görmez.
- **Saf fonksiyonlar** (`safeReturnUrl`, `problemOf`): doğrudan çağrılır.

### Kurallar

1. **Adresleri `environment.apiBaseUrl` ile kur.** Testlerin hangi ortam ayarıyla derlendiğine güvenme.
2. **Kaybolmuş bir ekrana bakma.** Başarılı bir girişten sonra yönlendirici başka ekrana geçer; eski ekranın öğesi elde kalır ama artık güncellenmez. Ekranın ara durumuna bakmak için isteği bekleyen bir `Promise` ile tut, sonra bitir.
3. **Bekle, sonra bak.** Ekran bir isteği ya da yönlendirmeyi başlatıp beklemeden döner; test `settle()` gibi kısa bir beklemeden ve `detectChanges()`'ten sonra okur.
4. **Menü ve bildirimler ekranın kendi öğesinin dışında çizilir** (sayfanın üstünde); onlar `document` üzerinden aranır.
5. **Kural 5 burada da geçerli:** test edilen kodu geçici olarak boz, testin kırmızıya döndüğünü gör, geri al.
6. **Bilinen boşluk:** karşı tarafın kodunda bulunan eksik düzeltilmez. Beklenen davranışı yazan test `it.skip` ile ve nedenini anlatan bir yorumla bırakılır, PR notuna yazılır.

