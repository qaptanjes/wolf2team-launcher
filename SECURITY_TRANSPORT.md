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

1. Son kaynak için Windows CI ve imzasız hazırlık artifact kontrollerini
   tamamlamak; bu deponun hazırlık çıktıları oyuncu sürümü değildir.
2. Canlı launcher dağıtımı ayrı doğrulanmalı; mevcut oyuncu kurulumları
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

## Güvensiz giriş arayüzü

Eski HTTP onay penceresi ve kabul durumu kaldırıldı. Güvensiz adresle
giriş denenince parola temizlenir, mevcut Türkçe HTTPS hata metni görünür
ve odak parola alanına döner. `InsecureLogin` artık userinfo içeren HTTPS
ve loopback FTP gibi yanlış adresleri de işaretler. Önceki derlemede bu iki
adres yanlışlıkla güvenli sayılıyordu; istek katmanındaki kontrol ise zaten
gönderimi engelliyordu. Artık UI ve istek katmanı aynı politikayı uygular.
Kullanılmayan TLS geri dönüşü alanı, yöntemi ve günlük ifadeleri temizlendi.

Tam kaynak testleri yine 198/198 başarılı. Ardından yalnız ekran kontrolüne
eklenen klavye/erişilebilirlik assertion'ları güncel kaynakla ayrı derlenip
çalıştırıldı. `LauncherScreens` parola alanındaki Enter ve başlat düğmesindeki
Space olayını gerçek kontrol işleyicisine iletir; parolanın temizlenmesi,
hata metni, erişilebilir ad ve aktif kontrolü denetler. Bunlar fiziksel tuş
basımı veya oyun içi test değildir. İki hata ekranı 1020×666 boyutunda
üretilip görsel olarak incelendi; metinler mevcut şeride sığıyor. Yerel ekran
görüntüleri ve özel oyun varlıkları Git'e veya CI artifact'ına eklenmedi.
Bağımsız kod incelemesinde engelleyici bulgu yok. Küçük test önerisi uygulandı:
her kısayol öncesinde giriş kontrolü seçilir ve aktif olduğu doğrulanır;
böylece Space denemesinin odağı gerçekten başlat düğmesinden parolaya
döndürdüğü kanıtlanır. Güncel ekran/klavye koşusu yeniden başarılıdır.

| UI kontrolü | Sonuç | Kanıt / sınır |
| --- | --- | --- |
| Enter/Space, hata metni, parola temizleme, aktif kontrol | PASS | Kontrol olayları ile ekran kontrolü; fiziksel klavye testi değil |
| Erişilebilir hata adı | PASS | `AccessibleName` gösterilen hatayla aynı |
| Hata metni kontrastı | PASS | `#F2ECE4`, alfa 200 siyah zemin: en parlak olası arka planda hesaplanan alt sınır 10,14:1 |
| Metin taşması | PASS | Font ölçüm testi ve iki güncel hata görüntüsünün incelenmesi |
| Tam Tab/Shift+Tab/Escape, ekran okuyucu, görünür odak | NOT RUN | Bu tur tam masaüstü erişilebilirlik kabulü yapılmadı |
| 320–1920 CSS px responsive | N/A | Web bileşeni değil; mevcut sabit 1020×666 Windows Forms penceresi korunuyor |
| Yüksek DPI ve farklı masaüstü çözünürlükleri | NOT RUN | Yalnız mevcut render ortamı kontrol edildi |
| Dark mode | N/A | Mevcut tek temalı başlatıcı; tema değişikliği yok |
| prefers-reduced-motion | N/A | Bu değişiklik animasyonsuz native hata akışı; genel hareket politikası değerlendirilmedi |
| TypeScript strict / web lazy-sizes / CWV | N/A | C# uygulaması; yeni web bundle veya asset yok |
| C# derleme | PASS | Mevcut C# 5 derleyicisi, warning-as-error; yeni bağımlılık yok |

10K kaynaklarının sinematik üretim ve web mimarisi aşamaları bu native hata
düzeltmesine uygulanmaz; entegrasyon girişindeki görev kapsamını koruma
kuralı kullanıldı. Okunabilirlik ve ölçülmeyen kontrolü başarılı saymama
ilkeleri uygulandı.

## Eski TLS protokollerinin devreden çıkarılması

Eski .NET ayarlarında SSL3, TLS 1.0 ve TLS 1.1 açıkken `Tls.Enable()`
yalnız TLS 1.2 bayrağını ekliyordu. Böylece eski protokoller yeni bağlantılar
için izin verilen kümede kalıyordu. Başlatıcı artık bu kümeyi TLS 1.2 ile
değiştirir. Desteklenmeyen çalışma ortamındaki kapalı kalma davranışı ve
Windows sertifika doğrulaması korunur; HTTP geri dönüşü yoktur.

Regresyon, gerçek `ServicePointManager` ayarına üç eski protokolü yükler,
başlatıcının başlatma yöntemini çağırır ve eski protokollerin kaldırılıp TLS 1.2'nin
korunduğunu denetler. Önceki kaynakta `expected 0, got 1008` ile başarısız,
düzeltmeyle başarılıdır. Global ayar ve kullanılabilirlik durumu `finally` ile
geri yüklenir. Tam kaynak koşusu **199 başarılı, sıfır hata**: Launcher 69,
Bootstrap 29, ConfigRepair 20, Setup 36, endpoint 30, paketleme ön kontrolü 15.
Bağımsız salt okunur incelemede engelleyici bulgu yoktur.

3 Kasım 2026 04:21 UTC'de güncel derlemenin gerçek `Net.Login` yolu ile
yayındaki HTTPS giriş adresine boş kullanıcı adı/parola gönderildi. Beklenen
kimlik doğrulama reddi alındı; oturum belirteci üretilmedi. Bu kontrol, mevcut
Windows ortamından genel HTTPS giriş yolunun erişilebilirliğini doğrular;
geçerli hesapla giriş, oyun içi karakter görünümü, tüm Windows sürümleri ve
TLS 1.3 kabulü **NOT RUN**. Özel adres ve deneme kaydı Git'e alınmadı.

Bu uygulama .NET 4 hedefini korur. Modern .NET hedeflerine geçildiğinde
Microsoft'un işletim sistemi varsayılan protokol seçimi önerisi ayrıca
değerlendirilmelidir; bu dar düzeltme framework yükseltmesi yapmaz.

Başvuru: [Microsoft .NET Framework TLS rehberi](https://learn.microsoft.com/en-us/dotnet/framework/network-programming/tls),
[OWASP TLS rehberi](https://cheatsheetseries.owasp.org/cheatsheets/Transport_Layer_Security_Cheat_Sheet.html),
[Microsoft yönlendirme politikası](https://learn.microsoft.com/en-us/dotnet/api/system.net.httpwebrequest.allowautoredirect?view=netframework-4.0).
