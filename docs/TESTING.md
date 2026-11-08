# Hasil pengujian

Semua angka di bawah berasal dari run CI nyata di GitHub Actions (`windows-latest`, Windows Server x64,
.NET 8, WinFsp 2.1.25156) — commit `090d6f8`, run
<https://github.com/aldiianshr-commits/SSD-VIRTUAL/actions/runs/42451360589>. Tidak ada angka yang diperkirakan.

## Ringkasan

| Tahap | Hasil |
|---|---|
| Build solusi (14 proyek, Release) | ✅ |
| Tes otomatis | ✅ **28 lulus, 0 gagal** |
| Publish self-contained win-x64 | ✅ `SsdVirtual.exe`, `SsdVirtualService.exe` |
| Installer Inno Setup | ✅ `SsdVirtual-Setup.exe` (49.8 MB) |
| Paket portabel | ✅ `SsdVirtual-Portable-x64.zip` (67.3 MB) |
| Smoke test installer | ✅ install senyap → `SsdVirtualService` **Running / Automatic** → uninstall senyap → layanan terhapus |

## Tes drive WinFsp (drive sungguhan, diakses lewat API file Windows biasa)

**DriveBehavesLikeALocalDisk** (±61 s)
- Drive tampil di `DriveInfo.GetDrives()`, `IsReady`, filesystem `NTFS`, label `SsdVirtual - Disk1`, kapasitas > 0.
- Buat folder bertingkat, tulis/append/baca teks, file 64 MB acak (MD5 cocok), tulis acak di tengah file (MD5 cocok).
- Rename, move file, move folder, copy, delete; hapus folder tidak kosong ditolak; hapus rekursif.
- 1.500 file kecil dalam satu folder, enumerasi tepat 1.500.
- Semua perubahan sampai ke "cloud" dengan isi identik; folder yang dihapus/dipindah tidak tersisa.

**SteamStyleLibraryWorks**
- `X:\SteamLibrary\steamapps\appmanifest_480.acf` dibuat dan terbaca oleh pemindai game.
- File 32 MB dialokasi lebih dulu lalu ditulis per-chunk tidak berurutan (pola Steam), diverifikasi per chunk.
- File yang dikunci (`FileShare.None`) menolak dibuka handle lain.

**AccountsAreIsolatedAndSurviveRemountAndOutages**
- Dua akun → dua drive; file di satu drive tidak muncul di drive lain maupun di cloud akun lain.
- File di-pin (Simpan offline) → cloud diputus → file tetap terbaca utuh; file baru ditulis saat offline; drive tetap terpasang.
- Setelah tersambung lagi, perubahan offline terunggah.
- Unmount → mount ulang (setara restart): namespace dan data kembali dari database & cache lokal.

## Tes volume (jalur namespace + cache + sinkronisasi tanpa driver)

- `LocalChangesAreUploadedAndVerified` — file kecil, 9 MB, dan 0 byte terunggah dan diverifikasi MD5.
- `RenameMoveDeleteReachTheCloud` — rename/move/delete berantai diterapkan berurutan di cloud.
- `CloudFilesAreListedAndReadThroughTheCache` — listing malas, baca acak, baca kedua 100% dari cache (0 panggilan cloud).
- `OfflineReadsUseCacheAndWritesAreQueued` — saat offline: data cache terbaca, data belum di-cache gagal cepat, tulisan diantrikan lalu terunggah.
- `PartialOverwriteOfCloudFileKeepsTheRest` — menulis 100 byte ke file cloud yang belum diunduh tidak merusak sisa file.
- `UnsyncedChangesSurviveRestart` — perubahan yang belum terunggah selamat saat proses ditutup dan dibuka lagi.
- `ConflictKeepsBothVersions` — perubahan lokal + perubahan cloud → kedua versi tersimpan (`notes (Conflict …).txt`).
- `EvictionNeverDropsDirtyOrPinnedData` — kuota cache ditegakkan tanpa membuang data yang di-pin.

## Tes unit

Bitmap blok, util path & sanitasi nama Windows, metadata SQLite (persisten, case-insensitive, jurnal, cache
index, lokasi cloud vs lokal), parser VDF/ACF Steam, tanda tangan unduhan TeraBox (dibandingkan dengan
implementasi referensi), ukuran chunk TeraBox, enkripsi DPAPI token/konfigurasi dan isolasi antar akun,
kelengkapan string Bahasa Indonesia ↔ English (kunci dan placeholder sama).

## Yang belum diuji (jujur)

- **Akun Google Drive dan TeraBox sungguhan.** CI tidak memiliki kredensial, jadi provider cloud asli belum
  dijalankan terhadap layanan aslinya. Seluruh jalur di atas memakai provider folder lokal yang mengikuti
  antarmuka yang sama. Uji pertama dengan akun nyata sebaiknya memakai data yang tidak penting.
- **Steam sungguhan** (menambah library lewat UI Steam, mengunduh dan menjalankan game) dan launcher lain.
- **Restart Windows sungguhan** — disimulasikan dengan unmount/mount dan restart proses; mount otomatis oleh
  layanan saat boot diuji sebatas layanan `Automatic` yang berjalan setelah instal.
- **UI WPF** — dikompilasi dan dipaketkan, tetapi tidak ada uji UI otomatis; belum dicoba di layar oleh manusia.
- Benchmark performa: tersedia di halaman Performance untuk diukur di PC Anda; belum ada angka dari perangkat nyata.
