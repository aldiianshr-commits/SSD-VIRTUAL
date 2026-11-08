# Menyiapkan OAuth client Google Drive

Google mewajibkan setiap aplikasi memakai OAuth client miliknya sendiri; client ID tidak bisa dibuat oleh
installer. Cukup dilakukan sekali.

1. Buka <https://console.cloud.google.com/> → buat project (mis. "SsdVirtual").
2. **APIs & Services → Library** → aktifkan **Google Drive API**.
3. **APIs & Services → OAuth consent screen** → User type *External* → isi nama aplikasi & email.
   Tambahkan scope `.../auth/drive`, lalu di **Test users** tambahkan semua akun Google yang akan dipakai.
4. **Credentials → Create credentials → OAuth client ID** → Application type **Desktop app** → Create →
   **Download JSON**.
5. Di SsdVirtual: **Settings → Google Drive OAuth client → Import client JSON…** → pilih file tadi → **Save settings**.
6. **Accounts → Add account → Google Drive → Sign in and mount**. Browser terbuka di accounts.google.com;
   setelah izin diberikan, drive langsung terpasang.

Catatan

* Selama aplikasi berstatus *Testing*, Google menampilkan peringatan "unverified app" dan refresh token
  kedaluwarsa setelah 7 hari (pengguna perlu **Sign in again**). Untuk pemakaian pribadi jangka panjang,
  ubah status ke *In production* (scope `drive` termasuk *restricted*, verifikasi Google diperlukan bila
  dipakai publik).
* Untuk build sendiri, client bisa ditanam saat build lewat secret repositori `GOOGLE_CLIENT_ID` dan
  `GOOGLE_CLIENT_SECRET` (lihat `.github/workflows/build.yml`).
* Google Docs/Sheets/Slides tidak ditampilkan karena tidak punya isi biner; file biasa (exe, zip, video, dsb.) tampil normal.
