# Correntra Downloader 0.4.0

## Yeni

- **Playlist'ler artık klasöre iner**: `youtube.com/…?list=…` gibi bağlantılar
  tek dosyaya çökmez; her bölüm numaralı ayrı dosya olarak (`001 - Şarkı.mp4`,
  …) playlist adıyla açılan alt klasöre kaydedilir.
- **Tarayıcıda sağ tık → "Correntra Downloader ile indir"**: eklenti, bağlantı,
  video ve sesler için sağ-tık menüsü ekler. Genel yakalama kapalıyken bile
  çalışır (açık niyet).
- **VirusTotal denetimi (isteğe bağlı)**: Ayarlar → Gizlilik'ten etkinleştirin.
  Tamamlanan indirmeler ~70 motorla kontrol edilir; yalnızca dosyanın SHA-256
  özeti gönderilir, dosyanın kendisi asla gitmez. Sonuç dosya adının altında
  görünür; tehdit kırmızı uyarır.
- **Durum çubuğunda hız grafiği**: toplam aktarım hızının hafif 60 örneklik
  çizgisi; saniyede en fazla iki kez yeniden çizilir.
- **Görsel dil**: tutarlı yazı ölçeği (11/13/15/21), yumuşak hover geçişleri,
  diyaloglar için 180 ms fade+rise girişi.
- `CONTRIBUTING.md`: katkı kuralları, PR kontrol listesi, güvenlik bildirimi.

## Düzeltmeler

- **Seçilen dil artık yeniden başlatınca sıfırlanmıyor**: açılış, sistem dili
  yerine Ayarlar'da kayıtlı dili kullanır.
- Hakkında penceresi gerçek sürümü gösterir ("Sürüm 0.1.0"a donmuştu).

## Notlar

- Kurulum: `Correntra-Setup-0.4.0.exe` — önceki sürümün üzerinden günceller.
- Tarayıcı eklentisini kullananlar: `browser-extension/` klasörünü Chrome'da
  yeniden yükleyin (sağ-tık menüsü için).
- Lisans FSL-1.1-MIT olarak yayınlanır; her sürüm 2 yıl sonra MIT'e döner.
