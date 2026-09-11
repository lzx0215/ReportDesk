"""Read exported menu/resource metadata; never assert that a name match proves an XML binding."""
import json
import pathlib
import xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parent.parent
evidence = root / 'artifacts/verification/his-materials'
menus = json.loads((evidence / 'menus.json').read_text(encoding='utf-8'))
resources = {r['RESOURCE_ID']: r for r in json.loads((evidence / 'resources.json').read_text(encoding='utf-8'))}
reports = json.loads((evidence / 'before/audit.json').read_text(encoding='utf-8-sig'))['rows']
by_id = {r['MENU_ID']: r for r in menus}
result = ET.Element('HisMenuCandidates', exported='2026-09-11')
count = 0
for report in reports:
    if report['classification'] != 'Report':
        continue
    hits = [m for m in menus if m['MENU_NAME'] == report['name'] and
            'Report.Common.Implement.ucCommonWindow' in (resources.get(m['RESOURCE_ID'], {}).get('WIN_NAME') or '')]
    if not hits:
        continue
    entry = ET.SubElement(result, 'Report', hash=report['hash'])
    for menu in hits:
        chain, seen, item = [], set(), menu
        while item and item['MENU_ID'] not in seen:
            seen.add(item['MENU_ID']); chain.append(item)
            item = by_id.get(item['PARENT_MENU_ID'])
        chain.reverse()
        loc = ET.SubElement(entry, 'Location', menu=str(menu['MENU_ID']), resource=str(menu['RESOURCE_ID']),
                            active=str(all(m['VALID_FLAG'] == '1' for m in chain)).lower())
        ET.SubElement(loc, 'Segment').text = '菜单组 ' + str(menu['GROUP_ID']) + '（组名未导出）'
        for m in chain:
            ET.SubElement(loc, 'Segment').text = m['MENU_NAME']
        count += 1
ET.indent(result)
output = root / 'src/ReportDesk.Core/Data/his-menu-candidates.xml'
output.parent.mkdir(parents=True, exist_ok=True)
ET.ElementTree(result).write(output, encoding='utf-8', xml_declaration=True)
print(json.dumps({'reports': len(result), 'paths': count, 'output': str(output)}))
