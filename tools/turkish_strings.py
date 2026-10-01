"""Lists the Turkish sentences in the API's code that a reader can see (J80).

    python tools/turkish_strings.py            # every one, as JSON
    python tools/turkish_strings.py --missing  # those not in Localization/en.json

Single-line string literals with a Turkish letter, outside comments. Files
whose Turkish is data or not for readers are skipped: the exercise catalogue
(seed data), log lines, the time-zone list, the brand in the sender address.
Mail bodies are raw string templates with their own per-language versions,
so they never appear here.
"""

import json
import os
import re
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), '..'))
SKIP_FILES = {
    'ExerciseCatalogue.cs', 'QuietHours.cs', 'EmailOptions.cs', 'Program.cs',
    'ResendEmailSender.cs', 'PartitionMaintenance.cs',
    'AccountEmails.cs', 'AuthEmails.cs', 'PersonalDetailEmails.cs',
}
LITERAL = re.compile(r'(?<![@$"])"((?:[^"\\\n]|\\.)*)"')
TURKISH = re.compile(r'[çğıöşüÇĞİÖŞÜ]')
BRAND = 'Çalış Bakalım Enik'


def sentences():
    found = []
    for d, _, files in os.walk(os.path.join(ROOT, 'src')):
        norm = d.replace('\\', '/')
        if '/obj' in norm or '/bin' in norm or 'Migrations' in norm:
            continue
        for f in sorted(files):
            if not f.endswith('.cs') or f in SKIP_FILES:
                continue
            for line in open(os.path.join(d, f), encoding='utf-8'):
                s = line.strip()
                if s.startswith('//') or s.startswith('*'):
                    continue
                for m in LITERAL.finditer(line):
                    v = m.group(1).replace('\\"', '"')
                    if TURKISH.search(v) and v != BRAND and v not in found:
                        found.append(v)
    return found


if __name__ == '__main__':
    out = sentences()
    if '--missing' in sys.argv:
        path = os.path.join(ROOT, 'src', 'CalisBakalimEnik.Application', 'Common',
                            'Localization', 'en.json')
        table = json.load(open(path, encoding='utf-8')) if os.path.exists(path) else {}
        out = [s for s in out if s not in table]
    sys.stdout.reconfigure(encoding='utf-8')
    print(json.dumps(out, ensure_ascii=False, indent=2))
