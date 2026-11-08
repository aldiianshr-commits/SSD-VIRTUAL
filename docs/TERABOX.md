# Integrasi TeraBox — apa adanya

TeraBox **tidak menyediakan API publik self-service**. "TeraBox Open Platform" hanya untuk mitra yang
mengajukan kerja sama, sehingga app key/OAuth resmi tidak bisa didapat oleh aplikasi umum.

Karena itu SsdVirtual memakai cara yang sah dan transparan:

1. Pengguna masuk sendiri di **halaman login resmi terabox.com** (WebView2, profil sementara yang terisolasi).
2. Setelah login berhasil, SsdVirtual menampilkan dialog persetujuan. Hanya bila pengguna menyetujui, cookie
   sesi terabox.com disimpan — terenkripsi DPAPI di service — dan tidak dikirim ke mana pun selain TeraBox.
3. Drive memakai endpoint web yang sama dengan browser (`/api/list`, `/api/quota`, `/api/filemanager`,
   `/api/precreate` → `superfile2` → `/api/create`, `/api/download`).

Batasan yang perlu diketahui

* Status: **eksperimental**. Endpoint tidak terdokumentasi dan bisa berubah kapan saja; ketentuan layanan
  TeraBox mungkin membatasi akses otomatis. Gunakan dengan risiko sendiri.
* Belum diuji dengan akun TeraBox sungguhan di pipeline ini (CI tidak memiliki kredensial). Pengujian
  otomatis memakai provider folder lokal untuk seluruh jalur filesystem/cache/sync.
* Kecepatan unduh akun gratis TeraBox dibatasi oleh TeraBox; pin game ke cache lokal sangat disarankan.
* Sesi kedaluwarsa → status akun menjadi "Perlu masuk ulang" → **Accounts → Sign in again**. Data lokal
  yang belum terunggah tetap aman dan diunggah setelah masuk ulang.
* TeraBox tidak menyediakan hash isi yang dapat dipercaya; setiap chunk upload diverifikasi MD5 oleh server.
