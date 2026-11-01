# -*- coding: utf-8 -*-
# แก้ตำแหน่ง: ย้ายการสุ่ม password ออกจาก ps_cmd expression
p = 'node_agent/omnilink_agent.py'
src = open(p, encoding='utf-8').read()
old = '''                    pfx_path = os.path.expanduser("~/omnilink/tls_cert.pfx")
                    ps_cmd = (
                        f"$cert = New-SelfSignedCertificate "
                        f"-DnsName '{socket.gethostname()}' "
                        f"-CertStoreLocation 'Cert:\\CurrentUser\\My' "
                        f"-KeyExportPolicy Exportable "
                        f"-NotAfter (Get-Date).AddYears(10); "
                        # SEC-INC-2026-11-01-001 (A1): password สุ่มต่อการสร้าง — ไม่มี literal
                        # (token_urlsafe = [A-Za-z0-9_-] ปลอดภัยใน single-quoted PowerShell string)
                        cert_export_pwd = secrets.token_urlsafe(24)
                        f"$pwd = ConvertTo-SecureString -String '{cert_export_pwd}' -Force -AsPlainText; "'''
assert old in src, "anchor"
new = '''                    pfx_path = os.path.expanduser("~/omnilink/tls_cert.pfx")
                    # SEC-INC-2026-11-01-001 (A1): password สุ่มต่อการสร้าง — ไม่มี literal
                    # (token_urlsafe = [A-Za-z0-9_-] ปลอดภัยใน single-quoted PowerShell string)
                    cert_export_pwd = secrets.token_urlsafe(24)
                    ps_cmd = (
                        f"$cert = New-SelfSignedCertificate "
                        f"-DnsName '{socket.gethostname()}' "
                        f"-CertStoreLocation 'Cert:\\CurrentUser\\My' "
                        f"-KeyExportPolicy Exportable "
                        f"-NotAfter (Get-Date).AddYears(10); "
                        f"$pwd = ConvertTo-SecureString -String '{cert_export_pwd}' -Force -AsPlainText; "'''
src = src.replace(old, new, 1)
open(p, 'w', encoding='utf-8').write(src)
print("OK")
