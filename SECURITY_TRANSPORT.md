# Launcher giriş taşıması — devam eden sertleştirme

Durum: **inceleme dalı; oyuncu sürümü olarak yayımlanmaz.**

Giriş yapılandırması TLS 1.2 bulunmadığında HTTPS adresini HTTP'ye düşürmez.
`Net.Login`, parola gövdesini oluşturmadan ve `WebRequest.Create` çağırmadan
önce adresi denetler. HTTPS veya geliştirme/test için HTTP loopback kabul
edilir; diğer şemalar, uzak HTTP, geçersiz adresler ve URI userinfo reddedilir.
HTTPS için TLS desteği yoksa mevcut Türkçe Windows güncelleme mesajı döner.
Normal TLS sertifika doğrulaması ve yönlendirme yasağı korunur.

Güncelleme indirme zaten HTTPS gerektiriyordu. Ortak `Net.StaticRequest`
artık haber, küçük resim ve güncelleme isteklerinde uzak HTTP, geçersiz
şema/adres ve URI userinfo değerlerini istek oluşmadan reddeder. TLS desteği
yoksa HTTPS adresi korunur ve istek güvenli hata verir. Haber adresi eski
HTTP adresine dönmez; geliştirme için loopback HTTP çalışmaya devam eder.
Haber görselleri yalnız haberin protokol/alan adı/portuyla aynı kaynaktan
alınır. Yönlendirme ve normal sertifika doğrulaması politikaları korunur.

## Test kapsamı

- Uzak HTTP ve TLS desteği bulunmayan HTTPS, istek oluşturma probuna ulaşmaz.
- Geçersiz adresler güvenli hata döndürür.
- HTTPS ve loopback HTTP adresindeki userinfo, istek oluşturma probuna ulaşmaz.
  Probun tam URI'yi yakaladığı önce ağ bağlantısı olmadan ayrıca doğrulanır.
- Gerçek loopback HTTP fixture'ı POST yöntemini, kodlanmış kullanıcı/parola
  gövdesini ve cevap token'ının ayrıştırılmasını kontrol eder.
- Yapılandırma testi, TLS bulunmadığında giriş, güncelleme ve haberin HTTPS
  kaldığını ve yanlış bir HTTP geri dönüşü günlüğü oluşmadığını doğrular.
- Ortak indirme kontrolü uzak HTTP, userinfo ve TLS eksikliği durumunda istek
  oluşturma probuna ulaşmaz; geçersiz adres hataları özel yolu yazmaz.
- Haber ve küçük resim çağrıları kontrolü atlayamaz. Gerçek loopback sunucusu
  üzerinden JSON ve PNG indirilip ayrıştırılır; küçük resim boyutu doğrulanır.
- Görsel adresleri HTTP'ye, farklı alan adına veya farklı porta geçemez;
  URI userinfo reddedilir, göreli aynı kaynak adresleri korunur.

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
2. Kullanılmayan eski TLS fallback UI/günlük bağlantılarını temizlemek.
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

Önceki kaynakta haberin HTTP geri dönüşü ortak indirme yolunu şifresiz
taşıyabiliyordu. Bu kaynak artık uzak HTTP isteğini reddeder. Yalnız eski
HTTP haber adresi bulunan kurulumda haberler yüklenmez; yayın öncesi HTTPS
`NewsUrl` adresinin gerçek dağıtım ayarlarında bulunduğu doğrulanmalıdır.

Tam yerel kaynak testinde Launcher 62, Bootstrap 29, ConfigRepair 20, Setup
36, endpoint 30 ve paketleme ön kontrolü 15: toplam 192 kontrol başarılıdır.
İmzasız hazırlık raporunun kaynak listesine yeni ön kontrol testi de eklenir.

Paketleme kaynağı `2ac43dc` için Windows hazırlık
[PR CI](https://github.com/qaptanjes/wolf2team-launcher/actions/runs/41574047454)
ve [push CI](https://github.com/qaptanjes/wolf2team-launcher/actions/runs/41574041975)
başarılıdır. Yerel temiz derleme üç imzasız EXE, altı hazırlık yalıtım testi,
kaynak/resource denetimi ve kurulum/onarım/kaldırma smoke testlerini geçti.
Bu sonuçlar sonraki haber güvenliği değişikliğinin CI sonucu değildir.

Haber güvenliği testlerinin eski kod koşusunda altı başarısızlık görüldü;
bunlardan biri testin varsayılan 443 portunu normalize etmeyen yanlış
beklentisiydi ve düzeltildi. Eski derlemede HTTP/farklı port/userinfo içeren
üç görsel adresinin de kabul edildiği ayrıca doğrulandı. Pozitif loopback
haber ve görsel testi eski kodda da başarılıydı.

Son tam kaynak koşusu: Launcher 68, Bootstrap 29, ConfigRepair 20, Setup 36,
endpoint 30 ve paketleme ön kontrolü 15; toplam **198 başarılı, sıfır hata**.
Bağımsız inceleme altı yeni test ile değişen TLS regresyonunu derlenmiş
çıktıda tekrar çalıştırdı: 7/7 başarılı, engelleyici bulgu yok. Gerçek TLS
el sıkışması ve canlı haber servisi bu yerel testlerin kapsamında değildir.

Başvuru: [OWASP TLS rehberi](https://cheatsheetseries.owasp.org/cheatsheets/Transport_Layer_Security_Cheat_Sheet.html),
[Microsoft yönlendirme politikası](https://learn.microsoft.com/en-us/dotnet/api/system.net.httpwebrequest.allowautoredirect?view=netframework-4.0).
