export LD_LIBRARY_PATH=/app/openssl/lib64

# 2. Ask OpenSSL to enumerate every KEM it can load
/app/openssl/bin/openssl list -kem-algorithms \
        -provider-path /app/openssl/lib64            \
        -provider oqsprovider -provider default
 
 echo "the filter list is"
        
        /app/openssl/bin/openssl list -kem-algorithms \
        -provider-path /app/openssl/lib64 \
        -provider oqsprovider -provider default |
awk '$1 !~ /^{/ {print $1}'


/app/openssl/bin/openssl s_client -connect cloudflare.com:443 \
        -groups X25519MLKEM768 \
          -provider-path /app/openssl/lib64 \
        -provider oqsprovider -provider default \
        -tls1_3 -msg -brief

