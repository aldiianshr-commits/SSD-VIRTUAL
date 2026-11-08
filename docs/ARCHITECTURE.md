# Arsitektur SsdVirtual

```
 ┌────────────────────────┐  named pipe (JSON)  ┌──────────────────────────────────────────────────┐
 │ SsdVirtual.exe (WPF)   │ ──────────────────► │ SsdVirtualService.exe (LocalSystem, auto start)  │
 │ UI, tray, OAuth login, │                     │  DriveManager ── per akun ──► MountedDrive        │
 │ TeraBox login (WebView2)│                    │                                ├ CloudFileSystem (WinFsp) ──► X:\
 └────────────────────────┘                     │                                ├ Volume (namespace + data)
                                                │                                │   ├ MetadataStore (SQLite WAL)
                                                │                                │   ├ CacheManager (sparse blobs)
                                                │                                │   └ ICloudProvider
                                                │                                │        ├ GoogleDriveProvider (API v3)
                                                │                                │        ├ TeraBoxProvider (web session)
                                                │                                │        └ LocalFolderProvider
                                                │                                └ SyncEngine (journal, upload, changes, pin)
                                                └──────────────────────────────────────────────────┘
```

## Modul

| Proyek | Isi |
|---|---|
| `SsdVirtual.Core` | Model (akun, pengaturan), path util, logger, kontrak IPC + `ServiceClient` |
| `SsdVirtual.Security` | DPAPI (`ProtectedFile`), `SecretStore` (token/cookie per akun), `ConfigStore` (konfigurasi terenkripsi) |
| `SsdVirtual.Database` | `MetadataStore`: pohon namespace di memori + SQLite (nodes, ops, upload_sessions, cache_files, kv) |
| `SsdVirtual.Cloud` | `ICloudProvider`, error model, retry HTTP, limiter bandwidth, statistik transfer, `LocalFolderProvider` |
| `SsdVirtual.GoogleDrive` | OAuth 2.0 PKCE loopback, refresh token, Drive API v3 (list, changes, resumable upload, range download) |
| `SsdVirtual.TeraBox` | Adapter sesi web TeraBox (list, quota, filemanager, precreate/superfile2/create, dlink) |
| `SsdVirtual.Cache` | `CacheManager` (blok 1 MiB, bitmap, read-ahead, de-dup fetch, pin, eviction LRU, verifikasi MD5), `TransferRegistry` |
| `SsdVirtual.Sync` | `Volume` (operasi namespace & data, listing, konflik) dan `SyncEngine` |
| `SsdVirtual.Filesystem` | `CloudFileSystem : Fsp.FileSystemBase` — implementasi WinFsp |
| `SsdVirtual.VirtualDrive` | `MountedDrive` (mount/unmount, label Explorer) dan `DriveManager` |
| `SsdVirtual.GameStorage` | Parser VDF/ACF Steam, pemindai game, pembuatan Steam Library |
| `SsdVirtual.Service` | Host Windows Service + server IPC |
| `SsdVirtual.Desktop` | UI WPF (10 halaman), lokalisasi, tray |

## Keputusan desain

**WinFsp (bukan Dokan).** Driver bertanda tangan, binding .NET resmi (`winfsp.net`), dukungan Mount Manager
(`\\.\X:`) sehingga drive tampil sebagai volume lokal. Nama filesystem dilaporkan `NTFS` untuk kompatibilitas
launcher game; `GetVolumeInfo` memberi kapasitas cloud dan ruang kosong = min(sisa cloud, ruang disk cache),
karena data baru ditampung di cache sebelum diunggah.

**Satu volume per akun.** Setiap akun punya `AccountConfig.Id` → folder cache `<CacheRoot>\<Id>` berisi
`metadata.db` dan `blobs\`. Tidak ada state bersama antar akun selain klien HTTP. Menghapus akun hanya
menghapus folder akun itu.

**Namespace.** Pohon node ada di memori (enumerasi direktori cepat) dan setiap perubahan ditulis ke SQLite
(WAL, `synchronous=FULL`). Direktori cloud di-list secara malas saat pertama kali dilalui. Setiap node
menyimpan lokasi cloud-nya sendiri (`RemoteParentId` + `RemoteName`), sehingga rename/move lokal langsung
terlihat dan operasi cloud bisa menyusul tanpa bergantung pada urutan.

**Cache.** Satu file *sparse* per file (`blobs\<id>.blk`) yang panjangnya selalu sama dengan ukuran file, plus
bitmap blok 1 MiB. Blok yang belum valid diambil dari cloud (digabung hingga 16 MiB per request, satu
request per blok walau banyak pembaca, read-ahead adaptif sampai 32 MiB). Penulisan masuk ke blob; blok yang
hanya tertimpa sebagian diambil dulu. `RemoteBacked` mencatat berapa byte awal yang masih punya salinan cloud
(truncate menurunkannya) sehingga extend/truncate tidak pernah membaca data cloud yang salah.

**Durabilitas.** Transisi file menjadi "dirty" (Version ≠ UploadedVersion) ditulis ke SQLite *sebelum*
operasi tulis kembali ke aplikasi. Setelah mati mendadak, `Volume.RecoverAfterCrash` memulihkan ukuran dari
blob dan `SyncEngine` mengunggah ulang semua yang dirty. Blob dirty atau pinned tidak pernah di-evict.

**Sinkronisasi.** Jurnal `ops` (mkdir/move/delete) diputar berurutan dengan backoff eksponensial dan tidak
pernah dibuang. Upload berjalan paralel setelah file ditutup dan diam (`UploadQuietSeconds`; write-through =
segera), resumable (session Google / uploadid TeraBox disimpan per versi), lalu diverifikasi MD5 bila cloud
menyediakan hash. Perubahan cloud: Google memakai Changes API; TeraBox/folder memakai listing ulang folder yang
baru dibuka. Konflik: versi cloud mempertahankan nama, versi lokal disimpan sebagai `nama (Conflict tanggal).ext`.
Perubahan dari cloud diberitahukan ke Explorer lewat `FspFileSystemNotify`.

**Keamanan.** Kredensial hanya ada di service, dienkripsi DPAPI (LocalMachine + entropy) dengan ACL
SYSTEM/Administrators; tidak pernah dikirim balik lewat IPC. Pipe IPC hanya untuk SYSTEM, Administrators
dan pengguna interaktif. OAuth memakai PKCE; login TeraBox memakai profil WebView2 sementara yang dihapus
setelahnya dan baru menyimpan cookie setelah persetujuan eksplisit.
