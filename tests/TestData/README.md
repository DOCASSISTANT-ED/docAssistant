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

## Değerlendirme seti için ek belgeler

`eval/questions.json` tek belgeyle anlamlı ölçüm veremediği için eklenen dört belge. Hepsi aynı uydurma şirkete (Örnek Lojistik A.Ş.) aittir ve bilerek ortak ifadeler içerir ("birim yöneticisinin yazılı onayı", "üç iş günü", "masraf formu ile muhasebe birimine"), ki arama yanlış belgeye gidebilsin.

| Dosya | Başlık 1 | Chunk | Tablo |
|---|---|---|---|
| `bilgi-guvenligi-politikasi` | Bilgi Güvenliği Politikası | 8 | Veri sınıfları (3 satır × 3 sütun) |
| `arac-kullanim-talimati` | Araç Kullanım Talimatı | 8 | Araç türüne göre bakım (3 × 3) |
| `satin-alma-proseduru` | Satın Alma Prosedürü | 8 | Tutara göre teklif ve onay (3 × 3) |
| `depo-is-guvenligi-talimati` | Depo İş Güvenliği Talimatı | 9 | Raf katına göre yük sınırı (3 × 3) |

Chunk sayıları DOCX hâli içindir. Başlık 1 belgenin adı sayıldığı için (#46) yüklemedeki ad ne olursa olsun aynıdır.

### Ortak yapı

- Başlık 1 (belge adı), ardından iki giriş paragrafı: kapsam ve sorumlu birim ("… Md.", "… Uzm.").
- Dört ya da beş "Bölüm N: …" başlığı (Başlık 2), bazılarının altında "N.M …" alt başlıkları (Başlık 3).
- Bir bölümde, ilk satırı başlık satırı olan bir tablo.
- "Bölüm 4"ün önünde sayfa sonu vardır. PDF'te "Bölüm 4" `satin-alma-proseduru` belgesinde 2. sayfada, diğer üçünde 3. sayfada başlar (bu PDF'ler üç sayfadır); önceki bölümlerin hepsi 1. sayfada başlar.

### Nasıl üretildi

DOCX dosyaları `ik-yonetmeligi.docx` şablon alınarak (aynı stiller, yazı tipleri ve sayfa düzeni) betikle üretildi; yazar bilgisi boştur. PDF'ler her DOCX'in Word'de açılıp "PDF olarak kaydet" ile kaydedilmesiyle alındı. PDF ve DOCX hâlleri aynı bölüm yollarını verir; PDF'te tablolar düz metin olarak gelir (#23). Bir belgenin metni değişirse iki hâli de yeniden üretilmeli ve `eval/questions.json` içindeki soruları gözden geçirilmelidir.

## Uzun bölümlü belge: `uzaktan-calisma-politikasi`

Yukarıdaki belgelerde her bölüm tek chunk'a sığdığı için chunk boyutunun etkisi ölçülemiyordu; bu belge bunun için eklendi (`docs/decisions.md` #47). Aynı uydurma şirkete aittir ve diğer belgelerle bilerek çakışır: kıdem şartı "en az altı ay" (Araç Kullanım Talimatı), çekirdek saatler 10:00–16:00 (İK Yönetmeliği'nde mesai 09:00–18:00), kayıp cihazın iki saat içinde bildirimi ve kişisel bilgisayar yasağı (Bilgi Güvenliği Politikası), "masraf formu ile muhasebe birimine" (İK Yönetmeliği, Araç Kullanım Talimatı).

| Bölüm | Karakter | Chunk (DOCX / PDF) |
|---|---|---|
| Giriş | ~480 | 1 / 1 |
| 1. Uygunluk ve Başvuru (1.1 dahil) | ~3200 | 3 / 3 |
| 2. Çalışma Düzeni (2.1, 2.2 dahil) | ~3300 | 4 / 5 |
| 3. Ekipman ve Bilgi Güvenliği | ~3300 | 3 / 3 |
| 4. Masraflar ve Destekler (4.1 dahil) | ~3000 | 3 / 3 |

Neyi sınamak için var:

- **Bölümün birden çok chunk'a bölünmesi:** her ana bölüm 2–3 chunk'tır; bazı soruların cevabı bölümün ikinci ya da sonraki chunk'ındadır.
- **Cümleden bölme:** "2. Çalışma Düzeni"ndeki ikinci paragraf tek başına ~1570 karakterdir (#43'teki 1500 sınırının üstünde) ve cümle sınırından bölünür.
- **DOCX ve PDF'te farklı chunk sınırları:** aynı paragraf PDF'te 2. sayfanın sonuna denk gelir, sayfa değişiminde bölünür; bu yüzden "2. Çalışma Düzeni" PDF'te bir chunk fazladır. Soruların kanıt metinleri (#47) iki biçimde de tek bir chunk'ta kalacak şekilde seçildi.
- **Sayfa altı bilgisi:** her sayfanın altında "UÇP-01 | Rev. 02 | Yürürlük: 01.03.2026 | Sayfa X / Y" vardır. DOCX parser sayfa altını okumaz; PDF'te bu satır metne karışır (#25).
- **Numaralı başlıklar:** "1. Uygunluk ve Başvuru", "2.1 Ulaşılabilirlik"; Word'ün Başlık 2/3 stilleriyle yazılıdır, PDF'te yazı boyutundan tanınır.
- **Tablo:** "3. Ekipman ve Bilgi Güvenliği"nde ekipman, karşılayan ve talep yolu (3 satır × 3 sütun, ilk satır başlık). Hücreler PDF'te satıra taşmayacak kadar kısadır.

Diğer belgeler gibi `ik-yonetmeligi.docx` şablon alınarak betikle üretildi (Başlık 1/2/3 stilleri, aynı sayfa düzeni; sayfa altı bilgisi eklendi), PDF'i Word'de "PDF olarak kaydet" ile alındı.
