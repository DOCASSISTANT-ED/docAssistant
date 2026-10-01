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

## Notlar

- Geliştirmede Ollama doğrudan Windows'a kurulur; compose'daki `ollama` servisi yalnızca `linux-gpu` profilinde çalışır.
