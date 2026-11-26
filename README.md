# Order Management API

## Idempotency

Create order memakai `Idempotency-Key` agar retry tidak membuat order baru. Key disimpan di database supaya tetap berlaku setelah restart. Hash payload mencegah key yang sama dipakai untuk data berbeda. Checkout baru tetap bisa memakai payload sama dengan key berbeda.

Order, pengurangan stok, dan key disimpan dalam satu transaksi, jadi request bersamaan tetap menghasilkan satu order.

## Database

SQLite dipilih karena cukup untuk prototipe dan tidak perlu server terpisah. Schema ada di `db/schema.sql`. Write memakai `BEGIN IMMEDIATE`, pengurangan stok memakai conditional update, dan perubahan status memeriksa versi order. SQLite hanya punya satu writer aktif, jadi untuk beban tulis besar perlu database server.

## Race condition tambahan

1. Harga produk berubah saat checkout. Harga dibaca dan disimpan ke item dalam transaksi yang sama, sehingga perubahan harga berikutnya tidak mengubah order lama.
2. Order baru masuk saat list dibaca. Count dan isi halaman memakai satu transaksi baca supaya melihat data yang sama.
