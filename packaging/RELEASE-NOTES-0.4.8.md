# Correntra Downloader 0.4.8

Heartbeat ve oynatma listesi düzeltmeleri.

## Öne çıkanlar

- Eklenti durumu artık gerçekten yeşile dönüyor: masaüstü, aracının
  gönderdiği eklenti sinyalini boru hattında düşürmüyor; durum çubuğu,
  hatırlatıcı ve kurulum sihirbazı gerçek bağlantıyı görüyor.
- Oynatma listesi öğeleri artık `001 - Titlemp4` gibi uzantısız inmiyor;
  numaralı dosyalar `001 - Başlık.mp4` olarak iniyor.
- `Correntra Baslat.exe` ~2.5 MB'a indi (önce ~94 MB); yine yalnızca
  yanındaki `baslat.bat` dosyasını çalıştırır.

## Highlights

- The extension status truly turns green now: the desktop no longer drops
  the agent's extension heartbeat on the pipe, so the status bar, reminder
  and setup wizard see the real connection.
- Playlist entries no longer land as `001 - Titlemp4` with no usable
  extension; numbered files arrive as `001 - Title.mp4`.
- `Correntra Baslat.exe` shrank to ~2.5 MB (was ~94 MB) and still only
  starts the adjacent `baslat.bat`.
