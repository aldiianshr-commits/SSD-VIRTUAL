# SsdVirtual

SsdVirtual mengubah akun **Google Drive** dan **TeraBox** menjadi drive lokal Windows (D:, E:, F:, …) yang
tampil di **This PC**, bisa dipakai oleh File Explorer, aplikasi apa pun, dan sebagai **Steam Library** —
dengan cache SSD lokal, sinkronisasi otomatis, dan mount permanen yang kembali sendiri setelah restart.

* Satu akun = satu drive dengan huruf, namespace, cache, dan sinkronisasi sendiri. Akun tidak pernah digabung.
* Drive dibuat oleh driver filesystem [WinFsp](https://winfsp.dev) dan dipasang oleh **SsdVirtualService**
  (Windows service, start otomatis) — tersedia sebelum login dan tanpa jendela aplikasi terbuka.
* Cache blok 1 MiB pada file *sparse*, read-ahead, **Simpan offline** (pin) untuk game, eviction LRU yang
  tidak pernah membuang data belum terunggah atau yang di-pin.
* Write-back/write-through dengan jurnal SQLite: perubahan lokal tidak hilang saat offline, crash, atau mati listrik.
* UI WPF gelap (Windows 11), 10 halaman, Bahasa Indonesia / English / bahasa kustom.

> **Status jujur:** jalur filesystem, cache, sinkronisasi, multi-akun, mount/remount, dan installer diuji
> otomatis di Windows (lihat [docs/TESTING.md](docs/TESTING.md)). Integrasi Google Drive dan TeraBox ditulis
> terhadap API sungguhan tetapi **belum diuji dengan akun sungguhan** di pipeline ini karena tidak ada
> kredensial. TeraBox bersifat **eksperimental** (tidak ada API publik — lihat [docs/TERABOX.md](docs/TERABOX.md)).

## Instalasi

1. Unduh `SsdVirtual-Setup.exe` (artefak CI `SsdVirtual-win-x64`) dan jalankan sebagai administrator.
   Installer memasang WinFsp (bila belum ada), aplikasi, layanan `SsdVirtualService` (auto start + restart
   otomatis bila crash), shortcut desktop, dan startup tray.
2. Google Drive memerlukan OAuth client milik Anda sendiri (sekali saja): [docs/GOOGLE_SETUP.md](docs/GOOGLE_SETUP.md).
3. Buka SsdVirtual → **Accounts → Add account** → pilih penyedia, huruf drive, ukuran cache → **Sign in and mount**.

Versi portabel: `SsdVirtual-Portable-x64.zip` → jalankan `install-service.ps1` sebagai administrator
(layanan dan driver tetap diperlukan untuk membuat drive).

## Steam dan game

1. **Game Storage → Create library folder** (membuat `X:\SteamLibrary`).
2. Steam → *Settings → Storage → Add drive* → pilih drive SsdVirtual.
3. Instal/pindahkan game ke drive itu, lalu **Simpan offline** agar seluruh file game ada di cache lokal.

Batasan nyata: game yang belum di-cache dibaca dari cloud sesuai kecepatan internet dan bisa tersendat.
Menginstal game besar membutuhkan ruang cache lokal minimal sebesar game tersebut (data ditampung lokal
sebelum diunggah). Ruang kosong yang dilaporkan drive = min(sisa cloud, sisa disk cache).

## Membangun dari source

Persyaratan: Windows 10/11 x64, .NET 8 SDK, WinFsp (untuk menjalankan/menguji), Inno Setup 6 (installer).

```powershell
dotnet build SsdVirtual.sln -c Release
dotnet test tests\SsdVirtual.Tests -c Release          # tes drive membutuhkan WinFsp + administrator
dotnet publish src\SsdVirtual.Desktop -c Release -r win-x64 --self-contained -o out\SsdVirtual
dotnet publish src\SsdVirtual.Service -c Release -r win-x64 --self-contained -o out\SsdVirtual
ISCC /DSourceDir=%CD%\out\SsdVirtual /DOutputDir=%CD%\dist installer\SsdVirtual.iss   # salin winfsp.msi ke out\SsdVirtual dulu
```

Pipeline lengkap (build → tes unit + tes drive WinFsp sungguhan → publish → installer → zip portabel →
uji install/uninstall) ada di `.github/workflows/build.yml`.

## Struktur

```
src/
  SsdVirtual.Core          model, IPC, logger, util
  SsdVirtual.Security      DPAPI, secret store, konfigurasi terenkripsi
  SsdVirtual.Database      metadata SQLite + jurnal
  SsdVirtual.Cloud         antarmuka provider, HTTP retry, limiter, provider folder
  SsdVirtual.GoogleDrive   OAuth 2.0 PKCE + Drive API v3
  SsdVirtual.TeraBox       adapter sesi web TeraBox (eksperimental)
  SsdVirtual.Cache         cache blok
  SsdVirtual.Sync          Volume + SyncEngine
  SsdVirtual.Filesystem    filesystem WinFsp
  SsdVirtual.VirtualDrive  mount & manajemen akun
  SsdVirtual.GameStorage   Steam library/manifest
  SsdVirtual.Service       SsdVirtualService.exe
  SsdVirtual.Desktop       SsdVirtual.exe (WPF)
tests/SsdVirtual.Tests     unit, volume, dan tes drive WinFsp
installer/                 Inno Setup + skrip portabel
docs/                      arsitektur, setup Google, TeraBox, hasil uji
```

Detail desain: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Lisensi

MIT. WinFsp berlisensi GPLv3 dengan *FLOSS exception* yang mengizinkan pemakaian oleh perangkat lunak
open-source berlisensi OSI; distribusi tertutup/komersial memerlukan lisensi komersial WinFsp.
Lihat [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
