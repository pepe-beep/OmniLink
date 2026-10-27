using System;
using System.Threading.Tasks;
using System.Windows;
using OmniLink.Core;
using OmniLink.Views.Settings;

namespace OmniLink.Views;

/// <summary>
/// v2.1 onboarding: flow จับคู่ (TLS + fingerprint TOFU + PIN + เพิ่มเข้า topology) แยกมาจาก
/// SecurityPairingPage เดิมเพื่อให้ AddFirstNodeWizard ใช้ร่วมกันได้ — ตรรกะเดิม 100%
/// (ไม่ copy: หน้า Pairing เรียกตัวนี้ต่อจากวันนี้เป็นต้นไป)
/// feedback = (ข้อความ, isError) บน UI thread · onDone(success) เรียกเมื่อจบ
/// </summary>
internal static class PairingFlow
{
    public static void Run(ISettingsHost host, DiscoveredNode targetNode, string pin,
        Action<string, bool>? feedback = null, Action<bool>? onDone = null)
    {
        Task.Run(async () =>
        {
            var result = await host.Discovery.RequestPairingAsync(targetNode, pin, 15, fingerprint =>
            {
                var approved = false;
                host.Self.Dispatcher.Invoke(() =>
                {
                    var fp = fingerprint.ToUpperInvariant();
                    var readable = $"{fp[..4]}-{fp.Substring(4, 4)}-{fp.Substring(8, 4)}-{fp.Substring(12, 4)} …";
                    var msg = Loc.T("pairing.fingerprint.body",
                        "Fingerprint ของเครื่องลูก (TLS SHA-256):\n\n{0}\n\nกรุณาเทียบกับ Fingerprint บนจอเครื่องลูก (notification 'OmniLink Pairing')\n\nตรงกัน → กด Yes · ไม่ตรง → กด No เพื่อยกเลิกทันที (อาจมีผู้แทรกกลาง)", readable);
                    approved = MessageBox.Show(host.Self, msg,
                        Loc.T("pairing.fingerprint.title", "ยืนยัน Fingerprint — Trust On First Use"),
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                    if (approved)
                    {
                        feedback?.Invoke(Loc.T("settings.pairing.fp.ok", "ยืนยัน fingerprint แล้ว — กำลังส่ง PIN ไปยังเครื่องลูก..."), false);
                    }
                });
                return approved;
            });

            _ = host.Self.Dispatcher.BeginInvoke(() =>
            {
                if (result.Success)
                {
                    feedback?.Invoke(Loc.T("settings.pairing.success", "จับคู่กับ {0} สำเร็จ! เพิ่มเข้า topology แล้ว", result.Name ?? targetNode.Name), false);

                    var nodeId = !string.IsNullOrEmpty(result.NodeId) ? result.NodeId
                        : (!string.IsNullOrEmpty(targetNode.Uuid) ? targetNode.Uuid : $"node-{targetNode.Ip.Replace('.', '-')}");
                    var nodeCfg = new NodeConfig
                    {
                        NodeId = nodeId,
                        Name = result.Name ?? targetNode.Name,
                        Ip = targetNode.Ip,
                        Width = result.Width > 0 ? result.Width : targetNode.Width,
                        Height = result.Height > 0 ? result.Height : targetNode.Height,
                        Position = "right",
                        Enabled = true,
                        Policy = OmniPolicy.Normalize(host.Config.NewNodePolicy), // §4.5: default เชิงทรัพยากรตาม config
                        AudioPlaybackEnabled = true,
                        AudioMicEnabled = true,
                        AuthToken = result.Token,
                        CertFingerprintSha256 = result.Fingerprint ?? "",
                    };
                    host.Kvm.AddOrUpdateNode(nodeCfg, result.Token);
                    host.SaveConfig();
                    host.RefreshUi();
                    host.SetBanner(Loc.T("banner.pairing.ok", "จับคู่กับ {0} สำเร็จแล้ว — พร้อมใช้งานทันที", nodeCfg.Name));
                }
                else
                {
                    feedback?.Invoke(Loc.T("settings.pairing.failed", "จับคู่ไม่สำเร็จ: {0}", result.Message), true);
                }
                onDone?.Invoke(result.Success);
            });
        });
    }
}
