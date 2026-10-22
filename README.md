# WolfTeam Diriliş — Başlatıcı / Launcher

Bu depo, [WolfTeam Diriliş](https://wolf2team.com) hayran sunucusunun açık kaynak başlatıcısını, ön yükleyicisini ve kurulum programını içerir. Kaynaklar denetim ve şeffaflık için yayımlanır. Oyun istemcisi ve telifli oyun verileri bu deponun kapsamı dışındadır.

## İçerik

- `launcher/csharp/` — C# / .NET Framework WinForms başlatıcı
- `launcher/bootstrap/` — ön yükleyici ve güncelleme katmanı
- `launcher/setup/` — kurulum programı
- `launcher/tests/` — regresyon testleri
- `launcher/signpath/` — imzasız hazırlık derlemesine ait ayarlar ve testler
- `ops/` — Windows derleme ve doğrulama betikleri

## Windows derlemesi

Windows PowerShell 5.1, Git ve .NET Framework 4.x x64 `csc.exe` gereklidir. SDK, NuGet veya oyun dosyası indirilmez. GitHub Actions iş akışı `windows-2025` üzerinde çalışır.

```powershell
# Uygulama testleri
powershell -NoProfile -File ops/build-setup.ps1 -TestsOnly

# Temiz, commit edilmiş kaynak ağacından imzasız hazırlık paketi
powershell -NoProfile -File ops/build-signpath.ps1
```

Hazırlık komutu regresyon ve adres doğrulama testlerini çalıştırır; üç EXE'yi kaynaklardan derler, paket içeriğini doğrular ve geçici bir dizinde kurulum/onarım/kaldırma testlerini yapar. `build/signpath/unsigned/` altında şu dosyalar oluşur:

- `WolfTeamDirilis.exe`
- `WolfTeamLauncher.exe`
- `WolfTeamDirilis-Setup.exe`
- `build-report.json`, `SHA256SUMS`, `DO-NOT-PUBLISH.txt`, `LICENSE`

Tekrar çalıştırırken yeni, boş bir çıktı dizini belirtin: `-BuildRoot build/signpath-next`. Mevcut çıktılar üzerine yazılmaz. GitHub Actions aynı dosyaları commit kimliğini taşıyan indirilebilir bir artifact olarak saklar; sürüm yayımlamaz veya sunucuya dağıtmaz.

**Bu EXE'ler imzasız hazırlık çıktılarıdır; oyunculara dağıtılmaz.** Oyun görseli, simgesi veya istemcisi içermez; adresleri çalışmayan `build-only.invalid` alanını kullanır. Mevcut canlı kurulumun veya başlatıcının yerine konmamalıdır. Normal sürüm betiklerinin ihtiyaç duyduğu telifli `bg.png` ve `wt.ico` bu depoya dahil edilmez; hazırlık derlemesi bunları kullanmaz.

## Kod imzalama

[Code Signing Policy](CODE_SIGNING_POLICY.md), kapsamı, kaynak doğrulamasını, güvenlik incelemesini ve gelecekteki onay adımlarını açıklar. İmzalama etkin değildir; SignPath kabulü veya sertifika alınmış olduğu iddia edilmez. `ops/signpath/` içindeki yapılandırmalar inceleme örnekleridir. Kod imzası, antivirüs veya tarayıcı uyarılarının ortadan kalkacağını garanti etmez.

Gerçek oyun istemcisi, telifli bileşenler, üçüncü taraf ikililer ve önceden mevcut EXE'ler bu iş akışında imzalanmaz veya paketlenmez. Gizli anahtarlar, sertifikalar ve erişim token'ları Git'e eklenmemelidir.

## English

Open-source launcher, bootstrap and installer for WolfTeam Diriliş. The Windows CI compiles three **unsigned preparation binaries** from this repository, tests them, and records source hashes and build provenance. It excludes game assets and existing executables, uses deliberately non-operational endpoints, and performs no signing, release publication or deployment. SignPath acceptance and a complete security/provenance review remain prerequisites for any future signing activation. See the [policy](CODE_SIGNING_POLICY.md).

## License

MIT — see [LICENSE](LICENSE). This license does not grant rights to proprietary game components.
