# WolfTeam Diriliş — Başlatıcı / Launcher

Bu depo, **WolfTeam Diriliş** adlı ticari olmayan, hayran yapımı özel WolfTeam sunucusunun ([wolf2team.com](https://wolf2team.com)) **açık kaynak başlatıcısı ve kurulum programıdır**.

Kaynak neden açık? **Şeffaflık ve güven** için: başlatıcı imzasız olduğunda tarayıcılar ve antivirüsler yanlışlıkla "virüs" uyarısı verebiliyor. Kaynağı herkese açarak (1) kodun ne yaptığı denetlenebilir, (2) **SignPath Foundation** gibi açık kaynak projelere ücretsiz Authenticode kod imzalama hizmetlerinden yararlanılabilir.

## İçerik
- `launcher/csharp/` — başlatıcı (C# / .NET Framework, WinForms)
- `launcher/setup/` — kurulum programı
- `launcher/bootstrap/` — önyükleyici (kendini güvenli güncelleyen katman)
- `launcher/tests/` — testler + ekran görüntüsü üreticileri
- `ops/build-*.ps1` — derleme (Windows'un kendi `csc.exe`'si; SDK/NuGet gerekmez)

## Derleme
```powershell
# testler
powershell -File ops\build-setup.ps1 -TestsOnly
```
Tam sürüm derlemesi, oyun istemcisinden türetilen görseller (`bg.png`, `wt.ico`) gerektirir; bunlar **telifli oldukları için bu depoda yoktur**. CI ve denetim için nötr yer tutucu görseller kullanılır.

## Güvenlik
Bu depoda hiçbir sunucu sırrı, indirme token'ı veya şifre yoktur. Oyun istemcisinin kendisi ve verileri de burada değildir.

---

## English (short)
Open-source **launcher & installer** for the non-commercial, fan-made WolfTeam private server [wolf2team.com](https://wolf2team.com). C# / .NET Framework WinForms, built with the system `csc.exe` (no SDK). Open-sourced for transparency and to qualify for **free Authenticode code signing** (e.g. SignPath Foundation), which stops browsers/AV from falsely flagging the unsigned build. No server secrets, download tokens or game client data are in this repo.

## License
MIT — see [LICENSE](LICENSE).
