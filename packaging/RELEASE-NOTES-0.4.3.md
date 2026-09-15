# Correntra Downloader 0.4.3

Browser-extension onboarding release.

## Öne çıkanlar

- Ana ekranın sağ üstünde her zaman görünen **“Tarayıcı eklentisini yükleyin!”**
  kartı eklendi. Bağlantı durumu ayrı gösterilir; kurulum veya onarım rehberi
  eklenti bağlı olsa bile erişilebilir kalır.
- Kurulum rehberi, Setup ve portable paketlerinin yanındaki gerçek
  `browser-extension` klasör yolunu gösterir, otomatik olarak panoya kopyalar
  ve ayrıca “Yolu kopyala” / “Klasörü aç” düğmeleri sunar.
- Chrome/Edge kurulumu sıradan kullanıcılar için açıkça anlatılır:
  Geliştirici modu → Paketlenmemiş öğe yükle → yolu adres çubuğuna yapıştır →
  klasörü seç.
- Eklenti kaldırıldıktan veya devre dışı bırakıldıktan sonra arayüz artık
  sonsuza kadar “bağlı” göstermez. 30 saniyelik doğrulanmış heartbeat ve
  45 saniyelik süre aşımıyla durum otomatik yenilenir.
- Eklenti kurulmadığında açılışta sağ altta, odağı çalmayan bir kurulum
  hatırlatıcısı gösterilir; kurulum sırasında eklenti bağlanırsa kendiliğinden
  kapanır.

## Highlights

- Added an always-visible **Install the browser extension!** card in the
  upper-right of the main view. Live connection status remains separate and
  the setup/repair guide stays available even after connection.
- The guide displays and copies the exact bundled `browser-extension` path
  for both Setup and portable installs, with dedicated copy/open actions and
  plain-language Chrome/Edge instructions.
- Removing or disabling the extension no longer leaves a false connected
  state forever; authenticated heartbeats now expire when not renewed.

After updating an existing unpacked installation, open
`chrome://extensions` (or `edge://extensions`) and reload Correntra Catch so
the browser uses version 0.4.3.
