# Launcher giriş taşıması — devam eden sertleştirme

Durum: **inceleme dalı; oyuncu sürümü olarak yayımlanmaz.**

Giriş yapılandırması TLS 1.2 bulunmadığında HTTPS adresini HTTP'ye düşürmez.
`Net.Login`, parola gövdesini oluşturmadan ve `WebRequest.Create` çağırmadan
önce adresi denetler. HTTPS veya geliştirme/test için HTTP loopback kabul
edilir; diğer şemalar, uzak HTTP, geçersiz adresler ve URI userinfo reddedilir.
HTTPS için TLS desteği yoksa mevcut Türkçe Windows güncelleme mesajı döner.
Normal TLS sertifika doğrulaması ve yönlendirme yasağı korunur.

Güncelleme indirme zaten HTTPS gerektiriyordu. Haberler için mevcut HTTP
fallback bu değişiklikte korunur; bu yol parola taşımaz. Fallback günlüğü
artık haber adresinin yalnız host/port bilgisini kullanır, özel yollar yazılmaz.

## Test kapsamı

- Uzak HTTP ve TLS desteği bulunmayan HTTPS, istek oluşturma probuna ulaşmaz.
- Geçersiz adresler güvenli hata döndürür.
- HTTPS ve loopback HTTP adresindeki userinfo, istek oluşturma probuna ulaşmaz.
  Probun tam URI'yi yakaladığı önce ağ bağlantısı olmadan ayrıca doğrulanır.
- Gerçek loopback HTTP fixture'ı POST yöntemini, kodlanmış kullanıcı/parola
  gövdesini ve cevap token'ının ayrıştırılmasını kontrol eder.
- Yapılandırma testi, TLS bulunmadığında giriş ve güncellemenin HTTPS kaldığını,
  yalnız haber adresinin eski HTTP adresine dönebildiğini doğrular.

Yeni testler gerçek kullanıcı parolası veya üretim adresi kullanmaz. Negatif
ağ testlerinde `WebRequest.RegisterPrefix` probu, gerçek bağlantı yerine
istek oluşturma girişimini yakalar. TLS el sıkışması veya gerçek sertifika
hatası bu probun kanıtladığı davranışlar değildir.

İlk eski-kod koşusunda HTTP, TLS, geçersiz adres ve yapılandırma beklentileri
başarısız oldu. Pozitif fixture'daki başlangıç dosya-yolu hatası düzeltildi;
bu fixture hatası üretim kusuru sayılmaz. İlk tam yeşil koşuda Launcher 61,
Bootstrap 29, ConfigRepair 20, Setup 36 ve endpoint 30 kontrol başarılıydı.
Userinfo/POST denetimleriyle tam koşuda Launcher 62, Bootstrap 29,
ConfigRepair 20, Setup 36 ve endpoint 30 kontrol başarılı oldu (177 toplam).
Bağımsız incelemede üretim güvenlik engeli bulunmadı; test sunucusunun bozuk
Content-Length için yakalanan IOException kullanması önerisi uygulandı.

## Yayından önce kalanlar

1. `MainForm` içindeki eski şifresiz giriş onayını kaldırıp, güvenli hata
   mesajıyla eşleştirmek; klavye ve mesaj görünürlüğünü kontrol etmek.
2. Paketleme ön kontrolünün son kaynak için Windows CI sonucunu doğrulamak.
3. Son kaynak için Windows CI ve imzasız hazırlık artifact kontrollerini
   tamamlamak; bu deponun hazırlık çıktıları oyuncu sürümü değildir.
4. Canlı launcher dağıtımı ayrı doğrulanmalı; mevcut oyuncu kurulumları
   bu kaynak değişikliğiyle kendiliğinden güncellenmiş sayılmaz. Eski sunucu
   HTTP ucunun kaldırılması güncel launcher geçişiyle birlikte ele alınmalı.

`main`, canlı kurulum, oyun binary'si ve kod imzalama bu dalda değiştirilmedi.
Windows CI `jesus` push'larında da mevcut imzasız hazırlık akışını çalıştırır.

## Paketleme ön kontrolü

Her iki oyuncu paketleme betiği `AllowHttpLogin` seçeneğini açıkça reddeder.
Kurulum üretimi ve `build-launcher.ps1 -PackageDir` çağrısı, dosya üretiminden
önce ortak `Read-LauncherReleaseEndpoints` kontrolünden geçer. HTTPS
`LoginUrl` ve `UpdateBaseUrl` zorunludur; `NewsUrl` ve `ServerHost` isteğe
bağlıdır ve verilirse doğrulanır. Hatalar adresleri veya özel yolları yazmaz.
`-TestsOnly` ve paket oluşturmayan launcher derlemesi üretim adresi istemez.

15 sentetik kontrol iki gerçek betiğin ön kontrolünü, kaldırılan seçeneği ve
adres ayrıştırmasını kapsar. Önceki betiklerin yalıtılmış kopyasında sekiz
ön kontrol başarısız olur; yeni betiklerde 15 kontrol geçer. Testler gerçek
oyun dosyası veya üretim kimlik bilgisi kullanmaz.

Eski istemciler için ana INI üretiminin HTTP alanları bu değişiklikte
korunmuştur; yeni paket zorunlu HTTPS `launcher/endpoints.ini` ile üretilir.
Yeni launcher'ın `Net.Login` kontrolü uzak HTTP girişini ayrıca reddeder.
Bu çalışma eski kurulumları güncellemiş veya sunucunun HTTP ucunu kapatmış
sayılmaz.

Haber adresinin eski HTTP geri dönüşü, ana INI'deki ortak indirme yolunu
şifresiz taşıyabilir. Haber isteği parola taşımıyor olsa da bu yolun gizliliği
sağlanmış değildir; HTTPS haber geçişi ayrı bir açık güvenlik işidir.

Tam yerel kaynak testinde Launcher 62, Bootstrap 29, ConfigRepair 20, Setup
36, endpoint 30 ve paketleme ön kontrolü 15: toplam 192 kontrol başarılıdır.
İmzasız hazırlık raporunun kaynak listesine yeni ön kontrol testi de eklenir.

Önceki kaynak `1020356` için Windows hazırlık CI koşusu
[41572823795](https://github.com/qaptanjes/wolf2team-launcher/actions/runs/41572823795)
başarılıdır; bu sonuç sonraki paketleme değişikliklerinin CI sonucu değildir.

Başvuru: [OWASP TLS rehberi](https://cheatsheetseries.owasp.org/cheatsheets/Transport_Layer_Security_Cheat_Sheet.html).
