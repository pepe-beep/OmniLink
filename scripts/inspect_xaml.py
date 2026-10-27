import re

content = open('src/OmniLink/Views/MainWindow.xaml', encoding='utf-8').read()
names = sorted(set(re.findall(r'x:Name="([^"]+)"', content)))
events = sorted(set(re.findall(r'(Click|SelectionChanged|ValueChanged|Checked|Unchecked|MouseLeftButtonUp|PreviewMouseWheel)="([^"]+)"', content)))

print(f"Total x:Name ({len(names)}):")
for n in names:
    print(f"  - {n}")

print(f"\nTotal Events ({len(events)}):")
for ev, handler in events:
    print(f"  - {ev}: {handler}")
