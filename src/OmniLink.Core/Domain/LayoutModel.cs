using System;
using System.Collections.Generic;
using OmniLink.Core.Strategies;

namespace OmniLink.Domain;

/// <summary>
/// พิกัดและขนาดหน้าจอบริสุทธิ์ (Domain Value Object)
/// รองรับพิกัดติดลบสำหรับหน้าจอด้านซ้ายหรือด้านบนของจอหลัก (Windows VirtualScreen)
/// </summary>
public readonly record struct ScreenPosition(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// ตัวแทนคอมพิวเตอร์หรือหน้าจอแต่ละโหนดในระบบ (Domain Entity)
/// บันทึกความสัมพันธ์เชิงมิติ (Topology Links: Left, Right, Up, Down)
/// </summary>
public sealed record MachineNode(
    string NodeId,
    string DisplayName,
    bool IsHost,
    ScreenPosition Bounds,
    string? LeftNodeId = null,
    string? RightNodeId = null,
    string? UpNodeId = null,
    string? DownNodeId = null);

/// <summary>
/// แบบจำลองโทโพโลยีการจัดวางหน้าจอหลายเครื่อง (Aggregate Root)
/// รองรับการเชื่อมต่อ 3 จอขึ้นไป (เช่น ThinkPad L440 ทางซ้าย, Host ตรงกลาง, External ทางขวา)
/// </summary>
public sealed class LayoutModel
{
    public Dictionary<string, MachineNode> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void AddOrUpdateNode(MachineNode node)
    {
        if (node == null) throw new ArgumentNullException(nameof(node));
        Nodes[node.NodeId] = node;
    }

    public string? GetTargetNode(string currentNodeId, ScreenEdge edge)
    {
        if (string.IsNullOrEmpty(currentNodeId) || !Nodes.TryGetValue(currentNodeId, out var node))
        {
            return null;
        }

        return edge switch
        {
            ScreenEdge.Left => node.LeftNodeId,
            ScreenEdge.Right => node.RightNodeId,
            ScreenEdge.Top => node.UpNodeId,
            ScreenEdge.Bottom => node.DownNodeId,
            _ => null
        };
    }

    public void LinkNodes(string sourceNodeId, ScreenEdge edge, string targetNodeId)
    {
        if (!Nodes.TryGetValue(sourceNodeId, out var source) || !Nodes.TryGetValue(targetNodeId, out var target))
        {
            return;
        }

        switch (edge)
        {
            case ScreenEdge.Left:
                Nodes[sourceNodeId] = source with { LeftNodeId = targetNodeId };
                Nodes[targetNodeId] = target with { RightNodeId = sourceNodeId };
                break;
            case ScreenEdge.Right:
                Nodes[sourceNodeId] = source with { RightNodeId = targetNodeId };
                Nodes[targetNodeId] = target with { LeftNodeId = sourceNodeId };
                break;
            case ScreenEdge.Top:
                Nodes[sourceNodeId] = source with { UpNodeId = targetNodeId };
                Nodes[targetNodeId] = target with { DownNodeId = sourceNodeId };
                break;
            case ScreenEdge.Bottom:
                Nodes[sourceNodeId] = source with { DownNodeId = targetNodeId };
                Nodes[targetNodeId] = target with { UpNodeId = sourceNodeId };
                break;
        }
    }
}
