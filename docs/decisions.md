# Proje Kararları

Başlangıç toplantısında alınan, sonradan değiştirmesi zor kararlar. Değişiklikler PR ile yapılır.

| # | Konu | Karar | Tarih |
|---|------|-------|-------|
| 1 | Proje adı / namespace | `DocAssistant` _(onaylanacak)_ | |
| 2 | Repo sahibi | _(organizasyon hesabı mı, kişisel mi?)_ | |
| 3 | Teknoloji sürümleri | .NET 10, Blazor Web App, PostgreSQL 17 + pgvector | |
| 4 | Branch stratejisi | `main` korumalı; `feature/...` branch + en az 1 onaylı PR | |
| 5 | Merge yöntemi | _(squash mı, rebase mi?)_ | |
| 6 | Commit formatı | Conventional Commits (`feat:`, `fix:`, `test:` ...) | |
| 7 | "Bitti" tanımı | Test yazılmış, CI yeşil, karşı taraf review etmiş | |
| 8 | Embedding modeli | `bge-m3` (Ollama) | |
| 9 | Dosya saklama | SeaweedFS (geliştirme), S3 uyumlu depolama (canlı). MinIO'dan vazgeçildi, gerekçe Notlar'da. | 2026-10-05 |. | 2026-10-05 |

## Notlar

- Geliştirmede Ollama doğrudan Windows'a kurulur; compose'daki `ollama` servisi yalnızca `linux-gpu` profilinde çalışır.
- #9 gerekçesi: MinIO Community Edition Aralık 2025'te bakım moduna alındı, Nisan 2026'da arşivlendi; resmi Docker imajları artık yayınlanmıyor ve Docker Hub'dan çekilemeyebiliyor.
