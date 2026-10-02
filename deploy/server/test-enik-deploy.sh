#!/bin/bash
# Sandbox test for enik-deploy. Run it ON THE SERVER as servrinuse (Git Bash on
# Windows fakes symlinks, so it cannot run there):
#
#   scp -P 1212 deploy/server/{enik-deploy,test-enik-deploy.sh} servrinuse@192.168.1.101:/tmp/
#   ssh -p 1212 servrinuse@192.168.1.101 'bash /tmp/test-enik-deploy.sh /tmp/enik-deploy'
#
# It runs as an ordinary user in a temp folder with systemctl, psql, curl and
# chown stubbed, signs bundles with a throwaway key, and changes nothing outside
# that folder, which it removes at the end. Read the output: each case prints
# what enik-deploy said and, for the slot cases, what it asked systemctl to do.
set -u
T=$(mktemp -d /tmp/edtest.XXXXXX)
trap 'rm -rf "$T"' EXIT
cd "$T"
mkdir -p bin etc/qa lib/incoming api web key
sed -e 's|^\[ "\$(id -u)" -eq 0 \].*||' \
    -e "s|^ETC=.*|ETC=$T/etc|" -e "s|^LIB=.*|LIB=$T/lib|" \
    -e "s|api) echo /var/www/calis_bakalim_enik_api ;;|api) echo $T/api ;;|" \
    -e "s|web) echo /var/www/calis_bakalim_enik ;;|web) echo $T/web ;;|" \
    -e "s|^export PATH=.*|export PATH=$T/bin:\$PATH|" \
    -e 's/seq 1 30/seq 1 2/; s/sleep 2/sleep 0/' \
    "$1" > ed
chmod +x ed
for s in journalctl chown; do printf '#!/bin/bash\necho "  [stub] %s $*" >&2\n' $s > bin/$s; done
# systemctl records what it was asked; is-enabled/is-active answer "off" when $T/off exists.
cat > bin/systemctl <<EOS
#!/bin/bash
echo "  [stub] systemctl \$*" >&2
echo "\$*" >> $T/systemctl.log
case "\$1" in is-enabled|is-active) [ -f $T/off ] && exit 1 || exit 0 ;; esac
exit 0
EOS
printf '#!/bin/bash\necho "  [stub] psql $*" >&2\n[ -f "%s/psqlfail" ] && exit 3\nexit 0\n' "$T" > bin/psql
printf '#!/bin/bash\n[ -f "%s/unhealthy" ] && exit 22\necho "{ok}"\n' "$T" > bin/curl
chmod +x bin/*
printf 'PGHOST=127.0.0.1\nPGPORT=5432\nPGDATABASE=x\nPGUSER_MIGRATIONS=m\nPGPASSWORD_MIGRATIONS=p\n' > etc/qa/db.env
ssh-keygen -q -t ed25519 -N "" -C test -f key/k
printf 'yk-laptop namespaces="enik-deploy" %s\n' "$(cut -d' ' -f1,2 key/k.pub)" > etc/deploy-signers
ssh-keygen -q -t ed25519 -N "" -C other -f key/other

mk() { # kind env id [signing key]
  rm -rf p; mkdir -p p/payload/canvaskit
  echo hi > p/payload/index.html; echo w > p/payload/canvaskit/canvaskit.wasm
  echo bin > p/payload/CalisBakalimEnik.Api; echo sql > p/payload/migrate.sql
  printf 'kind=%s\nenv=%s\nid=%s\n' "$1" "$2" "$3" > p/manifest.txt
  n="$1-$2-$3.tar.gz"
  tar -czf "lib/incoming/$n" -C p manifest.txt payload
  rm -f "lib/incoming/$n.sig"
  ssh-keygen -Y sign -f "${4:-key/k}" -n enik-deploy "lib/incoming/$n" 2>/dev/null
  echo "$n"
}
q() { grep -vE '\[stub\] (chown|systemctl|psql|journalctl)'; }
live() { echo "   live: api=$(readlink api/qa) web=$(readlink web/qa)"; }

mkdir -p api/releases/qa/8-145d74b0 web/releases/qa/19-migrated
ln -s releases/qa/8-145d74b0 api/qa; ln -s releases/qa/19-migrated web/qa
echo 8-145d74b0 > api/releases/qa/.history; echo 19-migrated > web/releases/qa/.history

echo "### 1 web install";   ./ed install "$(mk web qa 20-aaaaaaaa)" 2>&1 | q; live
echo "### 2 api install";   ./ed install "$(mk api qa 9-bbbbbbbb)" 2>&1 | q; live
echo "### 3 tampered";      n=$(mk api qa 10-cccccccc); echo x >> "lib/incoming/$n"; ./ed install "$n" 2>&1 | tail -1; live
echo "### 3b other key";    ./ed install "$(mk api qa 10-cccccccc key/other)" 2>&1 | tail -1; live
echo "### 4 older";         ./ed install "$(mk api qa 7-dddddddd)" 2>&1 | tail -1
echo "### 4b same as live"; ./ed install "$(mk api qa 9-bbbbbbbb)" 2>&1 | tail -1
echo "### 5 name/manifest"; n=$(mk api qa 11-eeeeeeee); mv "lib/incoming/$n" lib/incoming/api-qa-12-eeeeeeee.tar.gz; mv "lib/incoming/$n.sig" lib/incoming/api-qa-12-eeeeeeee.tar.gz.sig; ./ed install api-qa-12-eeeeeeee.tar.gz 2>&1 | tail -1
echo "### 6 unhealthy";     touch unhealthy; ./ed install "$(mk api qa 13-ffffffff)" 2>&1 | q | grep -E '^(!!|==>|   )'; rm unhealthy; live
echo "### 7 migration fails"; touch psqlfail; ./ed install "$(mk api qa 14-abababab)" 2>&1 | q | grep -E '^(!!|==>)'; rm psqlfail; live; echo "   releases: $(ls api/releases/qa | tr '\n' ' ')"
echo "### 8 status";        ./ed status 2>&1 | head -2
echo "### 8b rollback web"; ./ed rollback web qa 2>&1 | q; live
echo "### 9 prune";         for b in 30 31 32 33 34 35 36; do ./ed install "$(mk web qa $b-1234567$((b%10)))" > /dev/null 2>&1; sleep 1; done
                            echo "   releases: $(ls web/releases/qa | tr '\n' ' ')"; live
echo "### 10 prod dirty";   ./ed install "$(mk api prod 5-aaaaaaaa-dirty)" 2>&1 | tail -1
echo "### 11 bad name";     ./ed install '../etc/x.tar.gz' 2>&1 | tail -1
echo "### 12 symlink in incoming"; ln -s /etc/passwd lib/incoming/api-qa-99-aaaaaaaa.tar.gz; touch lib/incoming/api-qa-99-aaaaaaaa.tar.gz.sig; ./ed install api-qa-99-aaaaaaaa.tar.gz 2>&1 | tail -1
echo "### 13 incoming cleaned"; ls lib/incoming | grep -c 'web-qa-3[0-6]' || true

ops() { grep -vE "^is-" systemctl.log | sed 's/ qa_calis_bakalim_enik//' | tr '\n' ' '; echo; }
echo "### 14 switched-off slot: deploy starts it for the check, then stops it"
touch off; : > systemctl.log
./ed install "$(mk api qa 40-abcdef01)" 2>&1 | grep -E "^(==>|!!)"
echo "   systemctl: $(ops)"
echo "### 15 switched-off slot: a failed migration starts nothing"
: > systemctl.log; touch psqlfail
./ed install "$(mk api qa 41-abcdef02)" 2>&1 | grep -E "^(==>|!!)"
rm psqlfail; echo "   systemctl: $(ops)"
echo "### 16 switched-off slot: rollback starts, checks, stops"
: > systemctl.log
./ed rollback api qa 2>&1 | grep -E "^(==>|!!)"
echo "   systemctl: $(ops)"
echo "### 17 a running slot is left running"
rm -f off; : > systemctl.log
./ed install "$(mk api qa 42-abcdef03)" 2>&1 | grep -E "^(==>|!!)"
echo "   systemctl: $(ops)"
