# Test Belgeleri

Parser, chunking ve değerlendirme testlerinin kullandığı ortak örnek belgeler (`docs/decisions.md` #26, #27). İçerik uydurmadır; gerçek bir şirketin belgesi buraya konmaz.

## `ik-yonetmeligi.docx` ve `ik-yonetmeligi.pdf`

Aynı belgenin iki hâli. DOCX Word'de üretildi, PDF aynı belgeden Word'ün "PDF olarak kaydet" özelliğiyle alındı. Birini değiştirirsen diğerini de yeniden üret; ikisi aynı içeriği taşımalı.

### Yapı

| Sıra | Tür | İçerik | PDF sayfası |
|---|---|---|---|
| 1 | Başlık 1 | İK Yönetmeliği | 1 |
| 2 | Paragraf | "Bu yönetmelik, Örnek Lojistik A.Ş. …" (3 satır; "vb." içerir) | 1 |
| 3 | Paragraf | "Yönetmeliğin uygulanmasından …" (2 satır; "Md." içerir) | 1 |
| 4 | Başlık 2 | Bölüm 1: Çalışma Saatleri | 1 |
| 5 | Paragraf | "Haftalık çalışma süresi …" (5 satır) | 1 |
| 6 | Paragraf | "İşe geç kalan çalışan, …" (1 satır) | 1 |
| 7 | Başlık 3 | 1.1 Fazla Mesai | 1 |
| 8 | Paragraf | "Haftalık kırk beş saati aşan …" (3 satır) | 1 |
| 9 | Başlık 2 | Bölüm 2: Yıllık İzin | 1 |
| 10 | Paragraf | "Çalışanlar, işe giriş tarihinden …" (2 satır) | 1 |
| 11 | Tablo | 4 satır × 2 sütun, ilk satır başlık (aşağıda) | 1 |
| 12 | Paragraf | "İzin talepleri en az on gün …" (2 satır; "Dr." içerir) | 1 |
| 13 | Başlık 2 | Bölüm 3: Harcırah (önünde sayfa sonu var) | 2 |
| 14 | Paragraf | "Şehir dışı görevlendirmelerde …" (2 satır; "1.500 TL" içerir) | 2 |
| 15 | Paragraf | "Harcırah talepleri, …" (3 satır) | 2 |

Satır sayıları PDF'teki satırlardır; DOCX'te paragraflar tek parçadır.

### Tablo

| Kıdem | Yıllık izin günü |
|---|---|
| 1–5 yıl | 14 |
| 5–15 yıl | 20 |
| 15 yıl ve üzeri | 26 |

### Biçim

- Başlıklar Word'ün yerleşik "Başlık 1/2/3" stilleriyle işaretlidir.
- PDF'te yazı boyutları: Başlık 1 = 20 pt, Başlık 2 = 16 pt, Başlık 3 = 14 pt, gövde = 12 pt.
- Tablonun ilk satırı "başlık satırı" olarak işaretlidir ve kalındır.

### Neyi sınamak için var

- Türkçe karakterler (İ, ı, ş, ğ, ç, ö, ü) ve tire dışı işaretler (–).
- Birden fazla satıra yayılan paragraflar (PDF'te satırların paragrafa birleştirilmesi).
- Üç seviyeli başlık hiyerarşisi ve bölüm yolu.
- Cümle sonu olmayan noktalar: "A.Ş.", "vb.", "Md.", "Dr.", "1.500", "09:00", "1.1".
- Tablo (DOCX'te tablo bloğu; PDF'te Faz 2'de düz metin).
- Sayfa numarası (PDF'te iki sayfa; DOCX'te sayfa bilgisi yoktur).
