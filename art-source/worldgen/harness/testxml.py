"""Summarise a Unity NUnit result XML: python3 testxml.py TestResults-worldgen.xml"""
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
print(root.tag, {k: root.get(k) for k in ['result', 'total', 'passed', 'failed', 'duration']})
for tc in root.iter('test-case'):
    out = tc.find('output')
    msg = tc.find('failure/message')
    print(tc.get('result'), tc.get('name'), tc.get('duration'))
    if out is not None and out.text:
        print('   ', out.text.strip().replace('\n', '\n    ')[:2000])
    if msg is not None and msg.text:
        print('   FAIL:', msg.text.strip()[:800])
