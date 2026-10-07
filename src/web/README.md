# DocAssistant Web

DocAssistant'ın Angular arayüzü. API'den ayrı bir uygulamadır ve onunla yalnızca HTTP üzerinden konuşur (`docs/decisions.md` #41). Kurulum ve çalıştırma adımları kök dizindeki [README.md](../../README.md) dosyasındadır.

Komutlar bu klasörde (`src/web`) çalıştırılır.

| İş | Komut |
|---|---|
| Paketleri kur | `npm install` |
| Geliştirme sunucusu (http://localhost:4200) | `npm start` |
| Testler | `npm test` |
| Üretim derlemesi (`dist/`) | `npm run build` |

API adresi `src/environments/` altındadır: geliştirmede `http://localhost:5174`, üretim derlemesinde aynı kaynak (boş).
