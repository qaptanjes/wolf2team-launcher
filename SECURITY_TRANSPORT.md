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
2. Kurulum betiğindeki `AllowHttpLogin` seçeneğini HTTPS gerekliliğiyle
   uyumlu hale getirmek ve kurulum ayarlarının regresyonlarını çalıştırmak.
3. Son kaynak için Windows CI ve imzasız hazırlık artifact kontrollerini
   tamamlamak; bu deponun hazırlık çıktıları oyuncu sürümü değildir.
4. Canlı launcher dağıtımı ayrı doğrulanmalı; mevcut oyuncu kurulumları
   bu kaynak değişikliğiyle kendiliğinden güncellenmiş sayılmaz. Eski sunucu
   HTTP ucunun kaldırılması güncel launcher geçişiyle birlikte ele alınmalı.

`main`, canlı kurulum, oyun binary'si ve kod imzalama bu dalda değiştirilmedi.
Windows CI `jesus` push'larında da mevcut imzasız hazırlık akışını çalıştırır.

Başvuru: [OWASP TLS rehberi](https://cheatsheetseries.owasp.org/cheatsheets/Transport_Layer_Security_Cheat_Sheet.html).
