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

## Notlar

- Geliştirmede Ollama doğrudan Windows'a kurulur; compose'daki `ollama` servisi yalnızca `linux-gpu` profilinde çalışır.
- #9 gerekçesi: MinIO Community Edition Aralık 2025'te bakım moduna alındı, Nisan 2026'da arşivlendi; resmi Docker imajları artık yayınlanmıyor ve Docker Hub'dan çekilemeyebiliyor.
