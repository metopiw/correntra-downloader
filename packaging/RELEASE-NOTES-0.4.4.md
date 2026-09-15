# Correntra Downloader 0.4.4

Video-download reliability release.

## Öne çıkanlar

- **Ayarlar → Güncellemeler** içindeki düğme artık Correntra ile birlikte
  paketlenmiş **yt-dlp** medya motorunu da denetler ve günceller. YouTube,
  Instagram, X/Twitter ve benzeri siteler değiştiğinde, uygulamayı yeniden
  yayımlamayı beklemeden buradan güncelleyebilirsiniz.
- İndirilen yt-dlp dosyası kullanılmadan önce sürüm komutuyla doğrulanır.
  Güncelleme yalnızca Correntra'nın kendi klasöründeki dosyaya uygulanır;
  bilgisayarınızdaki başka bir yt-dlp kurulumu değiştirilmez.
- Video üzerindeki turuncu **“Bu videoyu indir”** çubuğuna kapatma (`×`)
  düğmesi eklendi. Video tam ekrandayken çubuk otomatik gizlenir.

## Highlights

- **Settings → Updates** now also checks and updates Correntra's bundled
  **yt-dlp** media engine, so extractor fixes for YouTube, Instagram, X/Twitter
  and similar sites are available without waiting for a desktop-app release.
- Every downloaded yt-dlp binary is run through its version command before it
  replaces the bundled sidecar; unrelated system/PATH installations are never
  modified.
- The orange **Download this video** overlay now has a close (`×`) button and
  automatically hides while video is fullscreen.

After updating an existing unpacked installation, open
`chrome://extensions` (or `edge://extensions`) and reload Correntra Catch so
the browser uses version 0.4.4.
